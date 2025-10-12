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

        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // ← ключ
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
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _flushRequests.Writer.WriteAsync(tcs, ct);
            await tcs.Task.WaitAsync(ct); // .NET 7/8 — корректно
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_opt.BasePath);
            var batch = new List<MessageLogRecord>(_opt.BatchSize);
            var nextFlush = DateTimeOffset.UtcNow.Add(_opt.FlushInterval);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // 1) Быстро выгребаем из очереди всё, что есть, до BatchSize
                    while (batch.Count < _opt.BatchSize && _queue.Reader.TryRead(out var item))
                        batch.Add(item);

                    // 2) Если батч пуст — ждём или сообщение, или запрос флеша, или таймер
                    if (batch.Count == 0)
                    {
                        var readMsgTask = _queue.Reader.ReadAsync(stoppingToken).AsTask();
                        var readFlushTask = _flushRequests.Reader.ReadAsync(stoppingToken).AsTask();
                        var delayTask = Task.Delay(_opt.FlushInterval, stoppingToken);

                        var completed = await Task.WhenAny(readMsgTask, readFlushTask, delayTask);
                        if (completed == readMsgTask)
                        {
                            batch.Add(await readMsgTask);
                        }
                        else if (completed == readFlushTask)
                        {
                            // === БАРЬЕРНЫЙ FLUSH ===
                            var tcs = await readFlushTask;

                            // 2.1) Вычистить всё, что сейчас есть в очереди, в batсh
                            while (_queue.Reader.TryRead(out var pending))
                                batch.Add(pending);

                            // 2.2) Если что-то накопили — дозаписать
                            if (batch.Count > 0)
                            {
                                await EnsureFileAsync(stoppingToken);
                                foreach (var rec in batch)
                                {
                                    var line = JsonSerializer.Serialize(rec, _json);
                                    await _writer!.WriteLineAsync(line.AsMemory(), stoppingToken);
                                }
                                batch.Clear();
                            }

                            // 2.3) Финальный flush на диск и ACK барьеру
                            if (_writer is not null) await _writer.FlushAsync();
                            tcs.TrySetResult(true);
                            continue; // и сразу в следующий цикл
                        }
                        // если сработал таймер — дадим шанс периодическому flush ниже
                    }

                    // 3) Если есть батч — пишем его
                    if (batch.Count > 0)
                    {
                        await EnsureFileAsync(stoppingToken);
                        foreach (var rec in batch)
                        {
                            var line = JsonSerializer.Serialize(rec, _json);
                            await _writer!.WriteLineAsync(line.AsMemory(), stoppingToken);
                        }
                        batch.Clear();
                    }

                    // 4) Периодический flush (для снижения потери при краше)
                    if (DateTimeOffset.UtcNow >= nextFlush)
                    {
                        if (_writer is not null) await _writer.FlushAsync();
                        nextFlush = DateTimeOffset.UtcNow.Add(_opt.FlushInterval);
                    }

                    // 5) Обработать накопившиеся запросы флеша (если вдруг пришли пачкой)
                    while (_flushRequests.Reader.TryRead(out var tcs))
                    {
                        // ещё раз подчистим очередь на всякий случай
                        while (_queue.Reader.TryRead(out var pending))
                            batch.Add(pending);

                        if (batch.Count > 0)
                        {
                            await EnsureFileAsync(stoppingToken);
                            foreach (var rec in batch)
                            {
                                var line = JsonSerializer.Serialize(rec, _json);
                                await _writer!.WriteLineAsync(line.AsMemory(), stoppingToken);
                            }
                            batch.Clear();
                        }

                        if (_writer is not null) await _writer.FlushAsync();
                        tcs.TrySetResult(true);
                    }
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "JsonlAuditSink failed");
            }
            finally
            {
                try { if (_writer is not null) { await _writer.FlushAsync(); _writer.Dispose(); } } catch { }
            }
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
