using HarborSentinel.AdminConsole.ViewModels;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarborSentinel.AdminConsole.Services;

/// <summary>
/// Reads and classifies HarborSentinel export files (.json / .jsonl.gz).
/// Returns a typed ExportContentBase subclass; never throws — falls back to RawJsonContent.
/// </summary>
public static class ExportFileReader
{
    private static readonly JsonSerializerOptions RawJsonFormatOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ─── Public API ───────────────────────────────────────────────────────────

    public static async Task<ExportContentBase> ReadAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var raw = await DecompressToStringAsync(filePath, ct);
            bool isJsonl = filePath.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)
                        || filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            return isJsonl ? ClassifyJsonl(raw, filePath) : ClassifySingleJson(raw, filePath);
        }
        catch (Exception ex)
        {
            return new RawJsonContent
            {
                FilePath  = filePath,
                TypeLabel = "Ошибка загрузки",
                Json      = string.Empty,
                ErrorMessage = ex.Message
            };
        }
    }

    public static async Task<ExportContentBase> ReadAsync(string fileName, byte[] content, CancellationToken ct)
    {
        try
        {
            var raw = await DecompressToStringAsync(fileName, content, ct);
            bool isJsonl = fileName.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            return isJsonl ? ClassifyJsonl(raw, fileName) : ClassifySingleJson(raw, fileName);
        }
        catch (Exception ex)
        {
            return new RawJsonContent
            {
                FilePath = fileName,
                TypeLabel = "Ошибка загрузки",
                Json = string.Empty,
                ErrorMessage = ex.Message
            };
        }
    }

    public static async Task<string> ReadRawAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var raw = await DecompressToStringAsync(filePath, ct);
            return FormatRaw(raw);
        }
        catch (Exception ex)
        {
            return $"// Ошибка чтения файла:\n// {ex.Message}";
        }
    }

    public static async Task<string> ReadRawAsync(string fileName, byte[] content, CancellationToken ct)
    {
        try
        {
            var raw = await DecompressToStringAsync(fileName, content, ct);
            return FormatRaw(raw);
        }
        catch (Exception ex)
        {
            return $"// Ошибка чтения файла:\n// {ex.Message}";
        }
    }

    // ─── Decompression ────────────────────────────────────────────────────────

    private static async Task<string> DecompressToStringAsync(string filePath, CancellationToken ct)
    {
        if (filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(filePath);
            return await ReadStringAsync(fs, isGzip: true, ct);
        }
        return await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
    }

    private static async Task<string> DecompressToStringAsync(string fileName, byte[] content, CancellationToken ct)
    {
        await using var ms = new MemoryStream(content, writable: false);
        return await ReadStringAsync(ms, fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase), ct);
    }

    private static async Task<string> ReadStringAsync(Stream stream, bool isGzip, CancellationToken ct)
    {
        if (isGzip)
        {
            using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false);
            using var sr = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
            return await sr.ReadToEndAsync(ct);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        return await reader.ReadToEndAsync(ct);
    }

    // ─── Classification ───────────────────────────────────────────────────────

    private static ExportContentBase ClassifySingleJson(string content, string filePath)
    {
        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is not null)
            {
                // FlaggedDialog: has "dialog" array OR "verdict" object
                if (root["dialog"] is JsonArray || root["verdict"] is JsonObject)
                    return ParseFlaggedDialog(root, filePath);
            }
            return new RawJsonContent
            {
                FilePath  = filePath,
                TypeLabel = "JSON (неизвестный формат)",
                Json      = FormatRaw(content)
            };
        }
        catch (Exception ex)
        {
            return new RawJsonContent
            {
                FilePath     = filePath,
                TypeLabel    = "JSON (ошибка парсинга)",
                Json         = content,
                ErrorMessage = ex.Message
            };
        }
    }

    private static ExportContentBase ClassifyJsonl(string content, string filePath)
    {
        var lines = SplitLines(content);
        if (lines.Count == 0)
            return new RawJsonContent { FilePath = filePath, TypeLabel = "Пустой файл", Json = string.Empty };

        JsonObject? meta = null;
        try { meta = JsonNode.Parse(lines[0]) as JsonObject; }
        catch { }

        if (meta is null || Str(meta, "kind") != "meta")
        {
            return new RawJsonContent
            {
                FilePath  = filePath,
                TypeLabel = "JSONL (неизвестная схема)",
                Json      = FormatRaw(content)
            };
        }

        // Discriminate by export_kind / export_type, then by first data-line kind
        var exportKind = Str(meta, "export_kind", "exportKind") ?? string.Empty;
        var exportType = Str(meta, "export_type", "exportType") ?? string.Empty;

        if (exportKind == "started_users")
            return ParseStartedUsers(lines, meta, filePath);
        if (exportType == "participants")
            return ParseParticipants(lines, meta, filePath);
        if (exportType == "user_messages")
            return ParseUserMessages(lines, meta, filePath);
        if (exportKind == "tracked_chat_messages")
            return ParseChatMessages(lines, meta, filePath);

        // Fallback: inspect first data line
        if (lines.Count > 1)
        {
            JsonObject? first = null;
            try { first = JsonNode.Parse(lines[1]) as JsonObject; }
            catch { }

            if (first is not null)
            {
                var k = Str(first, "kind") ?? string.Empty;
                if (k == "message")     return ParseChatMessages(lines, meta, filePath);
                if (k == "participant") return ParseParticipants(lines, meta, filePath);
                if (k == "started_user") return ParseStartedUsers(lines, meta, filePath);
            }
        }

        return new RawJsonContent
        {
            FilePath  = filePath,
            TypeLabel = "JSONL (неизвестный тип)",
            Json      = FormatRaw(content)
        };
    }

    // ─── FlaggedDialog ────────────────────────────────────────────────────────

    private static FlaggedDialogContent ParseFlaggedDialog(JsonObject root, string filePath)
    {
        var user    = root["user"]    as JsonObject;
        var chat    = root["chat"]    as JsonObject;
        var verdict = root["verdict"] as JsonObject;
        var dialog  = root["dialog"]  as JsonArray;

        long? subjectUserId = Long(user, "id");

        var userId = subjectUserId?.ToString() ?? "нет данных";
        var username = Str(user, "username");
        var firstName = Str(user, "firstName", "first_name") ?? "";
        var lastName  = Str(user, "lastName",  "last_name")  ?? "";

        var chatId    = Long(chat, "id")?.ToString() ?? "нет данных";
        var chatTitle = Str(chat, "title") ?? "(без названия)";

        var label      = Str(verdict, "label") ?? "нет";
        var confidence = Double(verdict, "confidence")?.ToString("0.00", CultureInfo.InvariantCulture) ?? "нет данных";
        var reason     = Str(verdict, "reason") ?? "нет данных";

        var messages = new List<FlaggedDialogMessageViewModel>();
        if (dialog is not null)
        {
            foreach (var item in dialog)
            {
                if (item is not JsonObject msg) continue;
                var ts       = FormatTs(Str(msg, "timestamp") ?? "");
                var msgUid   = Long(msg, "userId", "user_id");
                var msgUname = Str(msg, "username") ?? "";
                var text     = Str(msg, "text") ?? "(пусто)";

                messages.Add(new FlaggedDialogMessageViewModel
                {
                    Timestamp     = ts,
                    AuthorDisplay = BuildAuthor(msgUid, msgUname),
                    Text          = text,
                    IsSubjectUser = subjectUserId.HasValue && msgUid == subjectUserId
                });
            }
        }

        return new FlaggedDialogContent
        {
            FilePath  = filePath,
            TypeLabel = "Выгрузка диалога (flagged)",
            UserId    = userId,
            Username  = string.IsNullOrWhiteSpace(username) ? "нет" : $"@{username}",
            Person    = BuildPerson(firstName, lastName),
            ChatId    = chatId,
            ChatTitle = chatTitle,
            Label      = label,
            Confidence = confidence,
            Reason     = reason,
            Messages   = messages
        };
    }

    // ─── ChatMessages ─────────────────────────────────────────────────────────

    private static ChatMessagesContent ParseChatMessages(IList<string> lines, JsonObject meta, string filePath)
    {
        var chatId     = Long(meta, "chat_id", "chatId")?.ToString() ?? "нет данных";
        var exportedAt = FormatTs(Str(meta, "exported_at_utc", "exportedAtUtc") ?? "");

        var msgs = new List<ExportMessageViewModel>();
        string? sumMsgCount = null, sumParticCount = null, completedAt = null;
        string chatTitle = "";

        foreach (var line in lines.Skip(1))
        {
            var obj = TryParseObj(line);
            if (obj is null) continue;
            var k = Str(obj, "kind") ?? "";

            if (k == "message")
            {
                var ts        = FormatTs(Str(obj, "timestamp_utc", "timestampUtc") ?? "");
                var msgChat   = Str(obj, "chat_title", "chatTitle") ?? "";
                if (string.IsNullOrEmpty(chatTitle) && !string.IsNullOrEmpty(msgChat))
                    chatTitle = msgChat;
                var userId    = Long(obj, "user_id", "userId");
                var uname     = Str(obj, "user_username", "userUsername") ?? "";
                var fName     = Str(obj, "user_first_name", "userFirstName") ?? "";
                var lName     = Str(obj, "user_last_name",  "userLastName")  ?? "";
                var text      = Str(obj, "text") ?? "(пусто)";
                var msgId     = Long(obj, "message_id", "messageId")?.ToString() ?? "";

                msgs.Add(new ExportMessageViewModel
                {
                    TimestampUtc = ts,
                    UserDisplay  = BuildUser(userId, uname, fName, lName),
                    Text         = text,
                    MessageId    = msgId
                });
            }
            else if (k == "summary")
            {
                sumMsgCount   = Long(obj, "message_count",     "messageCount")?.ToString();
                sumParticCount = Long(obj, "participant_count", "participantCount")?.ToString();
                completedAt   = FormatTs(Str(obj, "completed_at_utc", "completedAtUtc") ?? "");
            }
        }

        return new ChatMessagesContent
        {
            FilePath       = filePath,
            TypeLabel      = "Выгрузка сообщений чата",
            ChatId         = chatId,
            ChatTitle      = chatTitle,
            ExportedAt     = exportedAt,
            Messages       = msgs,
            MessageCount   = sumMsgCount   ?? msgs.Count.ToString(),
            ParticipantCount = sumParticCount ?? "нет данных",
            CompletedAt    = completedAt   ?? "нет данных"
        };
    }

    // ─── Participants ─────────────────────────────────────────────────────────

    private static ParticipantsContent ParseParticipants(IList<string> lines, JsonObject meta, string filePath)
    {
        var chatId     = Long(meta, "chat_id", "chatId")?.ToString() ?? "нет данных";
        var exportedAt = FormatTs(Str(meta, "exported_at_utc", "exportedAtUtc") ?? "");

        var list = new List<ExportParticipantViewModel>();
        string? sumPart = null, sumAdmin = null, sumBot = null, completedAt = null;

        foreach (var line in lines.Skip(1))
        {
            var obj = TryParseObj(line);
            if (obj is null) continue;
            var k = Str(obj, "kind") ?? "";

            if (k == "participant")
            {
                var uid    = Long(obj, "user_id", "userId")?.ToString() ?? "нет данных";
                var uname  = Str(obj, "user_username", "userUsername", "username") ?? "";
                var fName  = Str(obj, "user_first_name", "userFirstName", "firstName") ?? "";
                var lName  = Str(obj, "user_last_name",  "userLastName",  "lastName")  ?? "";
                var msgCnt = Long(obj, "message_count", "messageCount")?.ToString() ?? "0";
                var fSeen  = FormatTs(Str(obj, "first_seen_utc", "firstSeenUtc") ?? "");
                var lSeen  = FormatTs(Str(obj, "last_seen_utc",  "lastSeenUtc")  ?? "");
                var isAdm  = Bool(obj, "is_admin", "isAdmin");
                var isBot  = Bool(obj, "is_bot",   "isBot");
                var status = Str(obj, "status") ?? "";

                list.Add(new ExportParticipantViewModel
                {
                    UserId       = uid,
                    Username     = string.IsNullOrWhiteSpace(uname) ? "нет" : $"@{uname}",
                    FirstName    = fName,
                    LastName     = lName,
                    MessageCount = msgCnt,
                    IsAdmin      = isAdm == true ? "да" : "нет",
                    IsBot        = isBot == true ? "да" : "нет",
                    Status       = string.IsNullOrWhiteSpace(status) ? "нет данных" : status,
                    FirstSeenUtc = fSeen,
                    LastSeenUtc  = lSeen
                });
            }
            else if (k == "summary")
            {
                sumPart    = Long(obj, "participant_count", "participantCount")?.ToString();
                sumAdmin   = Long(obj, "admin_count",       "adminCount")?.ToString();
                sumBot     = Long(obj, "bot_count",         "botCount")?.ToString();
                completedAt = FormatTs(Str(obj, "completed_at_utc", "completedAtUtc") ?? "");
            }
        }

        return new ParticipantsContent
        {
            FilePath         = filePath,
            TypeLabel        = "Выгрузка участников чата",
            ChatId           = chatId,
            ExportedAt       = exportedAt,
            Participants     = list,
            ParticipantCount = sumPart     ?? list.Count.ToString(),
            AdminCount       = sumAdmin    ?? "нет данных",
            BotCount         = sumBot      ?? "нет данных",
            CompletedAt      = completedAt ?? "нет данных"
        };
    }

    // ─── UserMessages ─────────────────────────────────────────────────────────

    private static UserMessagesContent ParseUserMessages(IList<string> lines, JsonObject meta, string filePath)
    {
        var userId     = Long(meta, "user_id", "userId")?.ToString() ?? "нет данных";
        var chatIdRaw  = Long(meta, "chat_id", "chatId");
        var chatScope  = chatIdRaw.HasValue ? $"Чат {chatIdRaw}" : "Все чаты";
        var exportedAt = FormatTs(Str(meta, "exported_at_utc", "exportedAtUtc") ?? "");

        var msgs = new List<ExportUserMessageViewModel>();
        string? sumMsg = null, sumChat = null, completedAt = null;

        foreach (var line in lines.Skip(1))
        {
            var obj = TryParseObj(line);
            if (obj is null) continue;
            var k = Str(obj, "kind") ?? "";

            if (k == "message")
            {
                var ts         = FormatTs(Str(obj, "timestamp_utc", "timestampUtc") ?? "");
                var chatTitle  = Str(obj, "chat_title", "chatTitle") ?? "";
                var chatDisplay = string.IsNullOrWhiteSpace(chatTitle)
                    ? (Long(obj, "chat_id", "chatId")?.ToString() ?? "")
                    : chatTitle;
                var text    = Str(obj, "text") ?? "(пусто)";
                var flagged = obj["flagged"] as JsonObject;
                var isFlagged = flagged is not null;
                var flagLabel = isFlagged ? Str(flagged!, "label") ?? "" : "";

                msgs.Add(new ExportUserMessageViewModel
                {
                    TimestampUtc = ts,
                    ChatDisplay  = chatDisplay,
                    Text         = text,
                    IsFlagged    = isFlagged ? "да" : "нет",
                    FlagLabel    = flagLabel
                });
            }
            else if (k == "summary")
            {
                sumMsg  = Long(obj, "message_count", "messageCount")?.ToString();
                sumChat = Long(obj, "chat_count",    "chatCount")?.ToString();
                completedAt = FormatTs(Str(obj, "completed_at_utc", "completedAtUtc") ?? "");
            }
        }

        return new UserMessagesContent
        {
            FilePath      = filePath,
            TypeLabel     = "Выгрузка сообщений пользователя",
            UserId        = userId,
            ChatScope     = chatScope,
            ExportedAt    = exportedAt,
            Messages      = msgs,
            MessageCount  = sumMsg  ?? msgs.Count.ToString(),
            ChatCount     = sumChat ?? "нет данных",
            CompletedAt   = completedAt ?? "нет данных"
        };
    }

    // ─── StartedUsers ─────────────────────────────────────────────────────────

    private static StartedUsersContent ParseStartedUsers(IList<string> lines, JsonObject meta, string filePath)
    {
        var exportedAt = FormatTs(Str(meta, "exported_at_utc", "exportedAtUtc") ?? "");

        var list = new List<ExportStartedUserViewModel>();
        string? sumUser = null, completedAt = null;

        foreach (var line in lines.Skip(1))
        {
            var obj = TryParseObj(line);
            if (obj is null) continue;
            var k = Str(obj, "kind") ?? "";

            if (k == "started_user")
            {
                var uid       = Long(obj, "user_id", "userId")?.ToString() ?? "нет данных";
                var uname     = Str(obj, "username") ?? "";
                var fName     = Str(obj, "first_name", "firstName") ?? "";
                var lName     = Str(obj, "last_name",  "lastName")  ?? "";
                var hasPhone  = Bool(obj, "has_phone", "hasPhone");
                var phoneMask = Str(obj, "phone_masked", "phoneMasked") ?? "";
                var startCnt  = Int(obj, "start_count", "startCount")?.ToString() ?? "0";
                var startedAt = FormatTs(Str(obj, "started_at_utc",      "startedAtUtc")     ?? "");
                var lastStart = FormatTs(Str(obj, "last_started_at_utc", "lastStartedAtUtc") ?? "");

                list.Add(new ExportStartedUserViewModel
                {
                    UserId        = uid,
                    Username      = string.IsNullOrWhiteSpace(uname) ? "нет" : $"@{uname}",
                    FirstName     = fName,
                    LastName      = lName,
                    HasPhone      = hasPhone == true ? "да" : "нет",
                    PhoneMasked   = string.IsNullOrWhiteSpace(phoneMask) ? "нет" : phoneMask,
                    StartCount    = startCnt,
                    StartedAt     = startedAt,
                    LastStartedAt = lastStart
                });
            }
            else if (k == "summary")
            {
                sumUser     = Long(obj, "user_count", "userCount")?.ToString();
                completedAt = FormatTs(Str(obj, "completed_at_utc", "completedAtUtc") ?? "");
            }
        }

        return new StartedUsersContent
        {
            FilePath    = filePath,
            TypeLabel   = "Выгрузка пользователей (private-start)",
            ExportedAt  = exportedAt,
            Users       = list,
            UserCount   = sumUser     ?? list.Count.ToString(),
            CompletedAt = completedAt ?? "нет данных"
        };
    }

    // ─── Raw formatting ───────────────────────────────────────────────────────

    private static string FormatRaw(string content)
    {
        const int rawCap = 300_000;

        // Single JSON
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (content.Length > rawCap)
                return content[..rawCap] +
                    $"\n\n// ... (показано {rawCap:N0} из {content.Length:N0} символов; файл слишком большой для форматирования)";
            var pretty = JsonSerializer.Serialize(doc.RootElement, RawJsonFormatOptions);
            return pretty.Length > rawCap
                ? pretty[..rawCap] + $"\n\n// ... ({pretty.Length:N0} символов после форматирования, показано {rawCap:N0})"
                : pretty;
        }
        catch { }

        // JSONL — format each line, cap at 2000 lines for performance
        const int cap = 2000;
        var sb = new StringBuilder();
        int count = 0;
        foreach (var raw in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (++count > cap)
            {
                sb.AppendLine();
                sb.AppendLine($"// ... (показаны первые {cap} строк, остальное скрыто для производительности)");
                break;
            }
            var trimmed = raw.Trim();
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                sb.AppendLine(JsonSerializer.Serialize(doc.RootElement, RawJsonFormatOptions));
                sb.AppendLine();
            }
            catch
            {
                sb.AppendLine(trimmed);
            }
        }
        return sb.ToString();
    }

    // ─── JSON helpers ─────────────────────────────────────────────────────────

    private static string? Str(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
            if (obj.TryGetPropertyValue(name, out var n) && n is not null)
            {
                var s = n.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        return null;
    }

    private static long? Long(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
            if (obj.TryGetPropertyValue(name, out var n) && n is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l))  return l;
                if (v.TryGetValue<int>(out  var i))  return i;
                if (long.TryParse(v.ToString(), out var p)) return p;
            }
        return null;
    }

    private static int? Int(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
            if (obj.TryGetPropertyValue(name, out var n) && n is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (int.TryParse(v.ToString(), out var p)) return p;
            }
        return null;
    }

    private static double? Double(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
            if (obj.TryGetPropertyValue(name, out var n) && n is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return d;
                if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            }
        return null;
    }

    private static bool? Bool(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
            if (obj.TryGetPropertyValue(name, out var n) && n is JsonValue v)
                if (v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static JsonObject? TryParseObj(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch { return null; }
    }

    private static List<string> SplitLines(string content)
        => content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                  .Select(l => l.Trim())
                  .Where(l => !string.IsNullOrEmpty(l))
                  .ToList();

    // ─── Display helpers ──────────────────────────────────────────────────────

    private static string FormatTs(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "нет данных";
        if (DateTimeOffset.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        return iso;
    }

    private static string BuildPerson(string firstName, string lastName)
    {
        var parts = new[] { firstName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p));
        var result = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(result) ? "нет" : result;
    }

    private static string BuildAuthor(long? userId, string username)
    {
        if (!string.IsNullOrWhiteSpace(username)) return $"@{username}";
        if (userId.HasValue) return $"ID {userId}";
        return "неизвестный";
    }

    private static string BuildUser(long? userId, string username, string firstName, string lastName)
    {
        var name = BuildPerson(firstName, lastName);
        if (!string.IsNullOrWhiteSpace(username))
            return string.IsNullOrWhiteSpace(name) ? $"@{username}" : $"@{username} ({name})";
        if (!string.IsNullOrWhiteSpace(name))
            return userId.HasValue ? $"{name} (ID {userId})" : name;
        return userId.HasValue ? $"ID {userId}" : "неизвестный";
    }
}
