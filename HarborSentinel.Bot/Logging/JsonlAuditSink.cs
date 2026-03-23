using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;


namespace HarborSentinel.Bot.Logging
{
    /// <summary>
    /// Фоновая батч-запись событий в JSONL (одна строка = один JSON).
    /// Ротация по дням, периодические flush, FileShare.Read (можно читать параллельно).
    /// </summary>
    public sealed class JsonlAuditSink : BackgroundService, IAuditSink
    {
        private readonly Channel<TaskCompletionSource<bool>> _flushRequests;

        private readonly ILogger<JsonlAuditSink> _log;
        private readonly StorageOptions _opt;
        private readonly Channel<MessageLogRecord> _queue;
        private Exception? _terminalError;

        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private string? _currentPath;
        private StreamWriter? _writer;

        public JsonlAuditSink(ILogger<JsonlAuditSink> log, IOptions<StorageOptions> opt)
        {
            _log = log;
            _opt = opt.Value ?? new StorageOptions();
            _queue = Channel.CreateBounded<MessageLogRecord>(new BoundedChannelOptions(_opt.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait // backpressure при всплесках
            });
            _flushRequests = Channel.CreateUnbounded<TaskCompletionSource<bool>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        }

        public ValueTask EnqueueAsync(MessageLogRecord record, CancellationToken ct)
            => _queue.Writer.WriteAsync(record, ct);
       
        public async Task FlushAsync(CancellationToken ct)
        {
            if (_terminalError is not null)
                throw new InvalidOperationException("Audit sink is not available.", _terminalError);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _flushRequests.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw new InvalidOperationException("Audit sink is not available.", _terminalError ?? ex);
            }

            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_opt.BasePath);
            var batch = new List<MessageLogRecord>(_opt.BatchSize);
            var nextFlush = DateTimeOffset.UtcNow.Add(_opt.FlushInterval);
            Exception? terminalError = null;

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Быстро выгребаем из очереди всё, что есть, до BatchSize
                    while (batch.Count < _opt.BatchSize && _queue.Reader.TryRead(out var item))
                        batch.Add(item);

                    // Если батч пуст — ждём или сообщение, или запрос флеша, или таймер
                    if (batch.Count == 0)
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        var waitToken = waitCts.Token;

                        // WaitToReadAsync не потребляет элементы канала, поэтому проигравшие ожидания
                        // не "утаскивают" сообщения/флеш-запросы.
                        var waitMessageTask = _queue.Reader.WaitToReadAsync(waitToken).AsTask();
                        var waitFlushTask = _flushRequests.Reader.WaitToReadAsync(waitToken).AsTask();
                        var delayTask = Task.Delay(_opt.FlushInterval, waitToken);

                        var completed = await Task.WhenAny(waitMessageTask, waitFlushTask, delayTask);
                        waitCts.Cancel(); // гарантированно отменяем проигравшие ожидания

                        if (completed == waitMessageTask &&
                            waitMessageTask.IsCompletedSuccessfully &&
                            waitMessageTask.Result)
                        {
                            while (batch.Count < _opt.BatchSize && _queue.Reader.TryRead(out var item))
                                batch.Add(item);
                        }
                        else if (completed == waitFlushTask &&
                                 waitFlushTask.IsCompletedSuccessfully &&
                                 waitFlushTask.Result)
                        {
                            while (_flushRequests.Reader.TryRead(out var tcs))
                                await FlushAndAckAsync(batch, tcs, stoppingToken);

                            continue; // и сразу в следующий цикл
                        }
                        // если сработал таймер — дадим шанс периодическому flush ниже
                    }

                    // Если есть батч — пишем его
                    if (batch.Count > 0)
                        await WriteBatchAsync(batch, stoppingToken);

                    // Периодический flush (для снижения потери при краше)
                    if (DateTimeOffset.UtcNow >= nextFlush)
                    {
                        if (_writer is not null) await _writer.FlushAsync();
                        nextFlush = DateTimeOffset.UtcNow.Add(_opt.FlushInterval);
                    }

                    // Обработать накопившиеся запросы флеша (если вдруг пришли пачкой)
                    while (_flushRequests.Reader.TryRead(out var tcs))
                        await FlushAndAckAsync(batch, tcs, stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
            catch (Exception ex)
            {
                terminalError = ex;
                _log.LogError(ex, "JsonlAuditSink failed");
            }
            finally
            {
                try
                {
                    while (_queue.Reader.TryRead(out var pending))
                        batch.Add(pending);

                    if (batch.Count > 0)
                        await WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);

                    if (_writer is not null)
                        await _writer.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    terminalError ??= ex;
                    _log.LogError(ex, "JsonlAuditSink failed during shutdown flush");
                }
                finally
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }

                _terminalError = terminalError;
                _flushRequests.Writer.TryComplete(terminalError);

                while (_flushRequests.Reader.TryRead(out var tcs))
                {
                    if (terminalError is null)
                    {
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        tcs.TrySetException(
                            new InvalidOperationException("Audit sink is not available.", terminalError));
                    }
                }
            }
        }

        private async Task WriteBatchAsync(List<MessageLogRecord> batch, CancellationToken ct)
        {
            if (batch.Count == 0)
                return;

            await EnsureFileAsync(ct);
            foreach (var rec in batch)
            {
                var line = JsonSerializer.Serialize(rec, _json);
                await _writer!.WriteLineAsync(line.AsMemory(), ct);
            }

            batch.Clear();
        }

        private async Task FlushAndAckAsync(
            List<MessageLogRecord> batch,
            TaskCompletionSource<bool> tcs,
            CancellationToken ct)
        {
            while (_queue.Reader.TryRead(out var pending))
                batch.Add(pending);

            await WriteBatchAsync(batch, ct);

            if (_writer is not null)
                await _writer.FlushAsync();

            tcs.TrySetResult(true);
        }

        private async Task EnsureFileAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var fileName = $"{_opt.FilePrefix}-{now:yyyy-MM-dd}.jsonl";
            var path = Path.Combine(_opt.BasePath, fileName);

            if (_writer is null || !string.Equals(path, _currentPath, StringComparison.Ordinal))
            {
                // Ротация: закрываем старый файл и открываем новый в режиме append
                try { if (_writer is not null) { await _writer.FlushAsync(); _writer.Dispose(); } } catch { }

                var fs = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read, // позволяем tail/чтение во время записи
                    bufferSize: 1 << 16,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);

                _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _currentPath = path;
                _log.LogInformation("Audit sink switched to file: {Path}", path);
            }
        }
    }
}
