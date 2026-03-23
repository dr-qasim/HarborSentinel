using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HarborSentinel.Viewer.Models;

namespace HarborSentinel.Viewer.Services;

public sealed class FileLoadReport
{
    public string FilePath { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public int TotalEntries { get; set; }
    public int ParsedEntries { get; set; }
    public int SkippedEntries { get; set; }
    public int ErrorEntries { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class LoadFilesResult
{
    public IReadOnlyList<LogRecordView> Records { get; init; } = Array.Empty<LogRecordView>();
    public IReadOnlyList<FileLoadReport> Reports { get; init; } = Array.Empty<FileLoadReport>();
}

public sealed class LogFileReader
{
    public async Task<LoadFilesResult> LoadFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken ct)
    {
        var records = new List<LogRecordView>();
        var reports = new List<FileLoadReport>();
        var uniquePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in uniquePaths)
        {
            ct.ThrowIfCancellationRequested();

            var report = new FileLoadReport
            {
                FilePath = path,
                SourceKind = GetSourceKind(path)
            };

            if (!File.Exists(path))
            {
                report.ErrorEntries = 1;
                report.ErrorMessage = "Файл не найден.";
                reports.Add(report);
                continue;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (extension == ".jsonl")
                {
                    await ReadJsonlAsync(path, isGzip: false, records, report, ct).ConfigureAwait(false);
                    reports.Add(report);
                    continue;
                }

                if (extension == ".gz" && path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))
                {
                    await ReadJsonlAsync(path, isGzip: true, records, report, ct).ConfigureAwait(false);
                    reports.Add(report);
                    continue;
                }

                if (extension == ".json")
                {
                    await ReadJsonAsync(path, records, report, ct).ConfigureAwait(false);
                    reports.Add(report);
                    continue;
                }

                report.ErrorEntries = 1;
                report.ErrorMessage = $"Неподдерживаемый формат: {Path.GetExtension(path)}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.ErrorEntries++;
                report.ErrorMessage = ex.Message;
            }

            reports.Add(report);
        }

        return new LoadFilesResult
        {
            Records = records
                .OrderByDescending(item => item.Timestamp ?? DateTimeOffset.MinValue)
                .ToArray(),
            Reports = reports
        };
    }

    private static async Task ReadJsonlAsync(
        string path,
        bool isGzip,
        List<LogRecordView> output,
        FileLoadReport report,
        CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(path);
        Stream stream = fileStream;
        if (isGzip)
        {
            stream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: false);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            report.TotalEntries++;
            var outcome = ParseJsonlRecord(path, line, out var item);
            if (outcome == JsonlParseOutcome.Parsed && item is not null)
            {
                output.Add(item);
                report.ParsedEntries++;
                continue;
            }

            if (outcome == JsonlParseOutcome.InvalidJson)
            {
                report.ErrorEntries++;
            }
            else
            {
                report.SkippedEntries++;
            }
        }
    }

    private static async Task ReadJsonAsync(
        string path,
        List<LogRecordView> output,
        FileLoadReport report,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                report.ErrorEntries++;
                report.ErrorMessage = "JSON-файл не является объектом.";
                return;
            }

            if (!root.TryGetProperty("dialog", out var dialog) || dialog.ValueKind != JsonValueKind.Array)
            {
                report.ErrorEntries++;
                report.ErrorMessage = "В JSON отсутствует массив dialog.";
                return;
            }

            foreach (var message in dialog.EnumerateArray())
            {
                report.TotalEntries++;
                if (message.ValueKind != JsonValueKind.Object)
                {
                    report.SkippedEntries++;
                    continue;
                }

                output.Add(new LogRecordView
                {
                    SourceFile = path,
                    SourceKind = "dialog_json",
                    Timestamp = TryParseDateTimeOffset(GetString(message, "timestamp")),
                    ChatId = GetInt64(message, "chat_id"),
                    ChatTitle = GetString(message, "chat_title"),
                    ChatUsername = null,
                    UserId = GetInt64(message, "user_id"),
                    Username = GetString(message, "username"),
                    FirstName = null,
                    LastName = null,
                    MessageId = null,
                    Text = GetString(message, "text"),
                    Flagged = false,
                    Label = null,
                    Confidence = null,
                    Reason = null,
                    RawJson = message.GetRawText()
                });
                report.ParsedEntries++;
            }
        }
        catch (JsonException ex)
        {
            report.ErrorEntries++;
            report.ErrorMessage = $"Некорректный JSON: {ex.Message}";
        }
    }

    private static JsonlParseOutcome ParseJsonlRecord(string sourceFile, string line, out LogRecordView? item)
    {
        item = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return JsonlParseOutcome.Unsupported;
            }

            if (root.TryGetProperty("kind", out var kindProp))
            {
                var kind = kindProp.GetString();
                if (!string.Equals(kind, "message", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonlParseOutcome.Unsupported;
                }

                item = ParseTrackedExportMessage(sourceFile, root, line);
                return JsonlParseOutcome.Parsed;
            }

            if (root.TryGetProperty("timestamp", out _))
            {
                item = ParseAuditMessage(sourceFile, root, line);
                return JsonlParseOutcome.Parsed;
            }

            return JsonlParseOutcome.Unsupported;
        }
        catch
        {
            return JsonlParseOutcome.InvalidJson;
        }
    }

    private static string GetSourceKind(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".jsonl")
        {
            return "jsonl";
        }

        if (extension == ".gz" && path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))
        {
            return "jsonl_gzip";
        }

        if (extension == ".json")
        {
            return "json";
        }

        return "unknown";
    }

    private enum JsonlParseOutcome
    {
        Parsed = 0,
        Unsupported = 1,
        InvalidJson = 2
    }

    private static LogRecordView ParseAuditMessage(string sourceFile, JsonElement root, string rawJson)
    {
        var ai = root.TryGetProperty("ai", out var aiProp) && aiProp.ValueKind == JsonValueKind.Object
            ? aiProp
            : default;

        var label = ai.ValueKind == JsonValueKind.Object ? GetString(ai, "label") : null;
        var confidence = ai.ValueKind == JsonValueKind.Object ? GetDouble(ai, "confidence") : null;
        var reason = ai.ValueKind == JsonValueKind.Object ? GetString(ai, "reason") : null;
        var flagged = ai.ValueKind == JsonValueKind.Object && GetBool(ai, "flagged") == true;

        return new LogRecordView
        {
            SourceFile = sourceFile,
            SourceKind = "audit_jsonl",
            Timestamp = TryParseDateTimeOffset(GetString(root, "timestamp")),
            ChatId = GetInt64(root, "chatId"),
            ChatTitle = GetString(root, "chatTitle"),
            ChatUsername = GetString(root, "chatUsername"),
            UserId = GetInt64(root, "fromId"),
            Username = GetString(root, "fromUsername"),
            FirstName = GetString(root, "fromFirstName"),
            LastName = GetString(root, "fromLastName"),
            MessageId = GetInt32(root, "messageId"),
            Text = GetString(root, "text"),
            Flagged = flagged,
            Label = label,
            Confidence = confidence,
            Reason = reason,
            RawJson = rawJson
        };
    }

    private static LogRecordView ParseTrackedExportMessage(string sourceFile, JsonElement root, string rawJson)
    {
        var flaggedElement = root.TryGetProperty("flagged", out var flaggedProp)
            ? flaggedProp
            : default;

        var flagged = flaggedElement.ValueKind == JsonValueKind.Object;
        var label = flagged ? GetString(flaggedElement, "label") : null;
        var confidence = flagged ? GetDouble(flaggedElement, "confidence") : null;
        var reason = flagged ? GetString(flaggedElement, "reason") : null;

        return new LogRecordView
        {
            SourceFile = sourceFile,
            SourceKind = "tracked_export_jsonl",
            Timestamp = TryParseDateTimeOffset(GetString(root, "timestamp_utc")),
            ChatId = GetInt64(root, "chat_id"),
            ChatTitle = GetString(root, "chat_title"),
            ChatUsername = GetString(root, "chat_username"),
            UserId = GetInt64(root, "user_id"),
            Username = GetString(root, "user_username"),
            FirstName = GetString(root, "user_first_name"),
            LastName = GetString(root, "user_last_name"),
            MessageId = GetInt32(root, "message_id"),
            Text = GetString(root, "text"),
            Flagged = flagged,
            Label = label,
            Confidence = confidence,
            Reason = reason,
            RawJson = rawJson
        };
    }

    private static string? GetString(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? GetBool(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            bool.TryParse(prop.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? GetInt64(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value))
        {
            return value;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? GetInt32(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
        {
            return value;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value))
        {
            return value;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto;
        }

        return null;
    }
}
