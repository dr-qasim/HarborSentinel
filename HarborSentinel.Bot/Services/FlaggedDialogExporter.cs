using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot.Types;

namespace HarborSentinel.Bot.Services;

public sealed class FlaggedDialogExporter : IFlaggedDialogExporter
{
    private readonly ILogger<FlaggedDialogExporter> _log;
    private readonly StorageOptions _opt;
    private readonly IRecentMessagesCache _cache;
    private readonly IMessageStore _store;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public FlaggedDialogExporter(ILogger<FlaggedDialogExporter> log, IOptions<StorageOptions> opt, IRecentMessagesCache cache, IMessageStore store)
    {
        _log = log;
        _opt = opt.Value;
        _cache = cache;
        _store = store;
    }

    public async Task<string> ExportAsync(MessageLogRecord trigger, long chatId, int daysBack, CancellationToken ct)
    {
        var baseDir = Path.GetFullPath(_opt.BasePath);
        var flaggedDir = Path.Combine(baseDir, _opt.FlaggedDir);
        Directory.CreateDirectory(flaggedDir);

        // История из БД за daysBack
        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, daysBack));
        var dbList = await _store.QueryUserMessagesAsync(chatId, trigger.FromId, sinceUtc, untilUtc: null, maxCount: 5000, ct);
        var collected = new List<DialogMessage>(dbList.Count);
        var dedup = new HashSet<DialogMessageKey>();
        foreach (var message in dbList)
            TryAddDialog(collected, dedup, message);

        var fromDb = collected.Count;
        _log.LogInformation("Exporter: collected {Count} messages from DB for user {User} in chat {Chat}",
            fromDb, trigger.FromId, chatId);

        // История из файлов за диапазон (как было)
        var sinceDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-Math.Max(0, daysBack)));
        var files = Directory.EnumerateFiles(baseDir, $"{_opt.FilePrefix}-*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(p => TryParseDate(Path.GetFileName(p), out var d) && d >= sinceDate)
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var file in files)
        {
            await foreach (var rec in ReadJsonl(file, ct))
            {
                if (rec.ChatId == chatId && rec.FromId == trigger.FromId)
                {
                    var msg = new DialogMessage
                    {
                        Timestamp = rec.Timestamp,
                        ChatId = rec.ChatId,
                        ChatTitle = rec.ChatTitle,
                        UserId = rec.FromId,
                        Username = rec.FromUsername,
                        Text = rec.Text
                    };

                    TryAddDialog(collected, dedup, msg);
                }
            }
        }
        var fromFiles = collected.Count - fromDb;
        _log.LogInformation("Exporter: collected {Count} messages from files for user {User} in chat {Chat}",
            fromFiles, trigger.FromId, chatId);

        // Недавние из кэша (берём ОДИН раз, как у тебя)
        var cached = _cache.GetRecent(chatId, trigger.FromId, window: TimeSpan.FromMinutes(30), maxCount: 500);
        var fromCache = cached.Count;
        _log.LogInformation("Exporter: recent cache has {Count} messages for user {User} in chat {Chat}",
            fromCache, trigger.FromId, chatId);

        // Merge + дедуп
        foreach (var d in cached)
        {
            TryAddDialog(collected, dedup, d);
        }
        _log.LogInformation("Exporter: merged total {Total} (db={Db}, files={Files}, cache={Cache}, +trigger pending)",
            collected.Count, fromDb, fromFiles, fromCache);

        // Гарантированно добавим триггерное сообщение
        var triggerMsg = new DialogMessage
        {
            Timestamp = trigger.Timestamp,
            ChatId = trigger.ChatId,
            ChatTitle = trigger.ChatTitle,
            UserId = trigger.FromId,
            Username = trigger.FromUsername,
            Text = trigger.Text
        };
        TryAddDialog(collected, dedup, triggerMsg);

        // Пакет для модератора
        var payload = new FlaggedDialogPayload
        {
            User = new FlaggedUser
            {
                Id = trigger.FromId,
                Username = trigger.FromUsername,
                FirstName = trigger.FromFirstName,
                LastName = trigger.FromLastName
            },
            Chat = new FlaggedChat
            {
                Id = trigger.ChatId,
                Title = trigger.ChatTitle,
                Username = trigger.ChatUsername
            },
            Verdict = new FlaggedVerdict
            {
                Label = trigger.Ai?.Label,
                Confidence = trigger.Ai?.Confidence,
                Reason = trigger.Ai?.Reason
            },
            Dialog = collected.OrderBy(d => d.Timestamp).ToList()
        };

        // по желанию — уникализировать имя файла (не перезаписывать)
        var outPath = Path.Combine(flaggedDir, $"{trigger.FromId}_chat_{chatId}_dialog.json");
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(payload, JsonOpt), Encoding.UTF8, ct);

        _log.LogInformation("Flagged dialog saved: {Path} (messages={Count})", outPath, collected.Count);
        return outPath;
    }

    // ---- helpers ----

    private static bool TryParseDate(string fileName, out DateOnly date)
    {
        // ожидаем формат: messages-YYYY-MM-DD.jsonl
        date = default;
        try
        {
            var name = Path.GetFileNameWithoutExtension(fileName); // messages-YYYY-MM-DD
            var parts = name.Split('-');                           // [messages, YYYY, MM, DD]
            if (parts.Length < 4) return false;
            var y = int.Parse(parts[1]);
            var m = int.Parse(parts[2]);
            var d = int.Parse(parts[3]);
            date = new DateOnly(y, m, d);
            return true;
        }
        catch { return false; }
    }
    
    private static readonly System.Text.Json.JsonSerializerOptions ReadJsonOpt = new()
    {
        PropertyNameCaseInsensitive = true // ключевой флаг
    };


    private static async IAsyncEnumerable<MessageLogRecord> ReadJsonl(
      string path,
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;

        while ((line = await sr.ReadLineAsync()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            MessageLogRecord? rec = null;
            try
            {
                rec = JsonSerializer.Deserialize<MessageLogRecord>(line, ReadJsonOpt);
            }
            catch
            {
                // пропускаем битые строки
            }

            if (rec is not null) yield return rec;
        }
    }

    private static bool TryAddDialog(
        List<DialogMessage> collected,
        HashSet<DialogMessageKey> dedup,
        DialogMessage message)
    {
        var key = new DialogMessageKey(
            message.Timestamp,
            message.ChatId,
            message.UserId,
            message.Text ?? string.Empty);

        if (!dedup.Add(key))
            return false;

        collected.Add(message);
        return true;
    }

    private readonly record struct DialogMessageKey(
        DateTimeOffset Timestamp,
        long ChatId,
        long? UserId,
        string Text);

}
