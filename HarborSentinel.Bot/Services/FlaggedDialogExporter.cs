using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Text.Encodings.Web;

namespace HarborSentinel.Bot.Services;

public sealed class FlaggedDialogExporter : IFlaggedDialogExporter
{
    private readonly ILogger<FlaggedDialogExporter> _log;
    private readonly StorageOptions _opt;
    private readonly IRecentMessagesCache _cache;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public FlaggedDialogExporter(ILogger<FlaggedDialogExporter> log, IOptions<StorageOptions> opt, IRecentMessagesCache cache)
    {
        _log = log;
        _opt = opt.Value;
        _cache = cache;
    }

    public async Task<string> ExportAsync(MessageLogRecord trigger, long chatId, int daysBack, CancellationToken ct)
    {
        var baseDir = Path.GetFullPath(_opt.BasePath);
        var flaggedDir = Path.Combine(baseDir, _opt.FlaggedDir);
        Directory.CreateDirectory(flaggedDir);

        var sinceDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-Math.Max(0, daysBack)));

        // 1) История из файлов за диапазон
        var files = Directory.EnumerateFiles(baseDir, $"{_opt.FilePrefix}-*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(p => TryParseDate(Path.GetFileName(p), out var d) && d >= sinceDate)
            .OrderBy(p => p, StringComparer.Ordinal);

        var collected = new List<DialogMessage>();

        // 1) История из файлов
        foreach (var file in files)
        {
            await foreach (var rec in ReadJsonl(file, ct))
            {
                if (rec.ChatId == chatId && rec.FromId == trigger.FromId)
                {
                    collected.Add(new DialogMessage
                    {
                        Timestamp = rec.Timestamp,
                        ChatId = rec.ChatId,
                        ChatTitle = rec.ChatTitle,
                        UserId = rec.FromId,
                        Username = rec.FromUsername,
                        Text = rec.Text
                    });
                }
            }
        }
        var fromFiles = collected.Count;
        _log.LogInformation("Exporter: collected {Count} messages from files for user {User} in chat {Chat}",
            fromFiles, trigger.FromId, chatId);

        // 2) Недавние из кэша (берём ОДИН раз)
        var cached = _cache.GetRecent(chatId, trigger.FromId, window: TimeSpan.FromMinutes(30), maxCount: 500);
        var fromCache = cached.Count;
        _log.LogInformation("Exporter: recent cache has {Count} messages for user {User} in chat {Chat}",
            fromCache, trigger.FromId, chatId);

        // 3) Merge + дедуп
        foreach (var d in cached)
        {
            if (!collected.Any(x =>
                    x.Timestamp == d.Timestamp &&
                    x.ChatId == d.ChatId &&
                    x.UserId == d.UserId &&
                    string.Equals(x.Text ?? "", d.Text ?? "", StringComparison.Ordinal)))
            {
                collected.Add(d);
            }
        }
        _log.LogInformation("Exporter: merged total {Total} (files={Files}, cache={Cache}, +trigger pending)",
            collected.Count, fromFiles, fromCache);


        // 3) Гарантированно добавим триггерное сообщение
        var triggerMsg = new DialogMessage
        {
            Timestamp = trigger.Timestamp,
            ChatId = trigger.ChatId,
            ChatTitle = trigger.ChatTitle,
            UserId = trigger.FromId,
            Username = trigger.FromUsername,
            Text = trigger.Text
        };
        if (!collected.Any(d =>
                d.Timestamp == triggerMsg.Timestamp &&
                d.ChatId == triggerMsg.ChatId &&
                d.UserId == triggerMsg.UserId &&
                string.Equals(d.Text, triggerMsg.Text, StringComparison.Ordinal)))
        {
            collected.Add(triggerMsg);
        }

        // 4) Пакет для модератора
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
        PropertyNameCaseInsensitive = true // <-- ключевой флаг
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

}
