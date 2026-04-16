using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HarborSentinel.Bot.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HarborSentinel.Bot.Services;

public sealed class TrackedChatExportService : ITrackedChatExportService
{
    private readonly ILogger<TrackedChatExportService> _log;
    private readonly string _dbPath;
    private readonly string _exportsDir;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TrackedChatExportService(
        ILogger<TrackedChatExportService> log,
        IOptions<DatabaseOptions> dbOpt,
        IOptions<StorageOptions> storageOpt)
    {
        _log = log;
        _dbPath = dbOpt.Value.DbPath;

        var basePath = storageOpt.Value.BasePath;
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = "data";

        var subDir = storageOpt.Value.TrackedExportsDir;
        if (string.IsNullOrWhiteSpace(subDir))
            subDir = "tracked_exports";

        _exportsDir = Path.Combine(Path.GetFullPath(basePath), subDir);
        Directory.CreateDirectory(_exportsDir);
    }

    public async Task<TrackedChatExportResult> ExportAsync(long chatId, CancellationToken ct)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var fileName = $"chat_{chatId}_{startedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz";
        var fullPath = Path.Combine(_exportsDir, fileName);

        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, timestamp_utc, chat_id, chat_title, chat_username, user_id,
       user_username, user_first_name, user_last_name, message_id, text
FROM messages
WHERE chat_id = $chat
ORDER BY timestamp_utc ASC, id ASC;";
        cmd.Parameters.AddWithValue("$chat", chatId);

        var participants = new HashSet<string>(StringComparer.Ordinal);
        long messageCount = 0;

        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gz, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var header = new
        {
            kind = "meta",
            chat_id = chatId,
            exported_at_utc = startedAtUtc.UtcDateTime.ToString("O"),
            format = "jsonl",
            version = 1
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOpt)).ConfigureAwait(false);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var storeId = reader.GetInt64(0);
            var timestampUtc = reader.GetString(1);
            var entryChatId = reader.GetInt64(2);
            var chatTitle = reader.IsDBNull(3) ? null : reader.GetString(3);
            var chatUsername = reader.IsDBNull(4) ? null : reader.GetString(4);
            long? userId = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            var username = reader.IsDBNull(6) ? null : reader.GetString(6);
            var firstName = reader.IsDBNull(7) ? null : reader.GetString(7);
            var lastName = reader.IsDBNull(8) ? null : reader.GetString(8);
            int? messageId = reader.IsDBNull(9) ? null : reader.GetInt32(9);
            var text = reader.IsDBNull(10) ? null : reader.GetString(10);

            participants.Add(BuildParticipantKey(userId, username, firstName, lastName));
            messageCount++;

            var line = new
            {
                kind = "message",
                store_id = storeId,
                timestamp_utc = timestampUtc,
                chat_id = entryChatId,
                chat_title = chatTitle,
                chat_username = chatUsername,
                message_id = messageId,
                user_id = userId,
                user_username = username,
                user_first_name = firstName,
                user_last_name = lastName,
                text = text
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOpt)).ConfigureAwait(false);
        }

        var summary = new
        {
            kind = "summary",
            chat_id = chatId,
            message_count = messageCount,
            participant_count = participants.Count,
            completed_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOpt)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        _log.LogInformation(
            "Tracked chat export saved: {Path} (chat={ChatId}, messages={MessageCount}, participants={ParticipantCount})",
            fullPath,
            chatId,
            messageCount,
            participants.Count);

        return new TrackedChatExportResult
        {
            FilePath = fullPath,
            MessageCount = messageCount,
            ParticipantCount = participants.Count
        };
    }

    public async Task<int> CountParticipantsAsync(long chatId, CancellationToken ct)
    {
        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH message_stats AS (
    SELECT user_id
    FROM messages
    WHERE chat_id = $chat
      AND user_id IS NOT NULL
    GROUP BY user_id
),
member_union AS (
    SELECT user_id FROM chat_members WHERE chat_id = $chat
    UNION
    SELECT user_id FROM message_stats
)
SELECT COUNT(1)
FROM member_union;";
        cmd.Parameters.AddWithValue("$chat", chatId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<TrackedParticipantEntry>> GetParticipantsPageAsync(
        long chatId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);

        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH message_stats AS (
    SELECT user_id,
           COUNT(1) AS message_count,
           MIN(timestamp_utc) AS first_seen_utc,
           MAX(timestamp_utc) AS last_seen_utc
    FROM messages
    WHERE chat_id = $chat
      AND user_id IS NOT NULL
    GROUP BY user_id
),
message_profile AS (
    SELECT user_id, user_username, user_first_name, user_last_name
    FROM (
        SELECT user_id, user_username, user_first_name, user_last_name,
               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY timestamp_utc DESC, id DESC) AS rn
        FROM messages
        WHERE chat_id = $chat
          AND user_id IS NOT NULL
    ) t
    WHERE rn = 1
),
member_union AS (
    SELECT user_id FROM chat_members WHERE chat_id = $chat
    UNION
    SELECT user_id FROM message_stats
)
SELECT u.user_id,
       COALESCE(cm.username, mp.user_username) AS username,
       COALESCE(cm.first_name, mp.user_first_name) AS first_name,
       COALESCE(cm.last_name, mp.user_last_name) AS last_name,
       COALESCE(ms.message_count, 0) AS message_count,
       COALESCE(ms.first_seen_utc, cm.first_seen_utc, cm.last_seen_utc) AS first_seen_utc,
       COALESCE(cm.last_seen_utc, ms.last_seen_utc, cm.first_seen_utc) AS last_seen_utc,
       COALESCE(cm.is_admin, 0) AS is_admin,
       COALESCE(cm.is_bot, 0) AS is_bot,
       cm.status,
       CASE WHEN COALESCE(cm.seen_in_messages, 0) = 1 OR ms.user_id IS NOT NULL THEN 1 ELSE 0 END AS seen_in_messages,
       COALESCE(cm.seen_in_new_chat_members, 0) AS seen_in_new_chat_members,
       COALESCE(cm.seen_in_chat_member_updates, 0) AS seen_in_chat_member_updates,
       COALESCE(cm.seen_in_admin_snapshot, 0) AS seen_in_admin_snapshot
FROM member_union u
LEFT JOIN chat_members cm ON cm.chat_id = $chat AND cm.user_id = u.user_id
LEFT JOIN message_stats ms ON ms.user_id = u.user_id
LEFT JOIN message_profile mp ON mp.user_id = u.user_id
ORDER BY COALESCE(cm.is_admin, 0) DESC,
         COALESCE(cm.last_seen_utc, ms.last_seen_utc, cm.first_seen_utc, ms.first_seen_utc) DESC,
         u.user_id ASC
LIMIT $lim OFFSET $off;";

        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$lim", pageSize);
        cmd.Parameters.AddWithValue("$off", (page - 1) * pageSize);

        var list = new List<TrackedParticipantEntry>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TrackedParticipantEntry
            {
                UserId = reader.GetInt64(0),
                Username = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstName = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastName = reader.IsDBNull(3) ? null : reader.GetString(3),
                MessageCount = reader.GetInt64(4),
                FirstSeenUtc = DateTimeOffset.Parse(reader.GetString(5)),
                LastSeenUtc = DateTimeOffset.Parse(reader.GetString(6)),
                IsAdmin = reader.GetInt64(7) == 1,
                IsBot = reader.GetInt64(8) == 1,
                Status = reader.IsDBNull(9) ? null : reader.GetString(9),
                SeenInMessages = reader.GetInt64(10) == 1,
                SeenInNewChatMembers = reader.GetInt64(11) == 1,
                SeenInChatMemberUpdates = reader.GetInt64(12) == 1,
                SeenInAdminSnapshot = reader.GetInt64(13) == 1
            });
        }

        return list;
    }

    public async Task<TrackedParticipantsExportResult> ExportParticipantsAsync(long chatId, CancellationToken ct)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var fileName = $"chat_{chatId}_participants_{startedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz";
        var fullPath = Path.Combine(_exportsDir, fileName);

        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH message_stats AS (
    SELECT user_id,
           COUNT(1) AS message_count,
           MIN(timestamp_utc) AS first_seen_utc,
           MAX(timestamp_utc) AS last_seen_utc
    FROM messages
    WHERE chat_id = $chat
      AND user_id IS NOT NULL
    GROUP BY user_id
),
message_profile AS (
    SELECT user_id, user_username, user_first_name, user_last_name
    FROM (
        SELECT user_id, user_username, user_first_name, user_last_name,
               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY timestamp_utc DESC, id DESC) AS rn
        FROM messages
        WHERE chat_id = $chat
          AND user_id IS NOT NULL
    ) t
    WHERE rn = 1
),
member_union AS (
    SELECT user_id FROM chat_members WHERE chat_id = $chat
    UNION
    SELECT user_id FROM message_stats
)
SELECT u.user_id,
       COALESCE(cm.username, mp.user_username) AS username,
       COALESCE(cm.first_name, mp.user_first_name) AS first_name,
       COALESCE(cm.last_name, mp.user_last_name) AS last_name,
       COALESCE(ms.message_count, 0) AS message_count,
       COALESCE(ms.first_seen_utc, cm.first_seen_utc, cm.last_seen_utc) AS first_seen_utc,
       COALESCE(cm.last_seen_utc, ms.last_seen_utc, cm.first_seen_utc) AS last_seen_utc,
       COALESCE(cm.is_admin, 0) AS is_admin,
       COALESCE(cm.is_bot, 0) AS is_bot,
       cm.status,
       CASE WHEN COALESCE(cm.seen_in_messages, 0) = 1 OR ms.user_id IS NOT NULL THEN 1 ELSE 0 END AS seen_in_messages,
       COALESCE(cm.seen_in_new_chat_members, 0) AS seen_in_new_chat_members,
       COALESCE(cm.seen_in_chat_member_updates, 0) AS seen_in_chat_member_updates,
       COALESCE(cm.seen_in_admin_snapshot, 0) AS seen_in_admin_snapshot
FROM member_union u
LEFT JOIN chat_members cm ON cm.chat_id = $chat AND cm.user_id = u.user_id
LEFT JOIN message_stats ms ON ms.user_id = u.user_id
LEFT JOIN message_profile mp ON mp.user_id = u.user_id
ORDER BY COALESCE(cm.is_admin, 0) DESC,
         COALESCE(cm.last_seen_utc, ms.last_seen_utc, cm.first_seen_utc, ms.first_seen_utc) DESC,
         u.user_id ASC;";
        cmd.Parameters.AddWithValue("$chat", chatId);

        long participantCount = 0;
        long adminCount = 0;
        long botCount = 0;
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gz, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var header = new
        {
            kind = "meta",
            export_type = "participants",
            chat_id = chatId,
            exported_at_utc = startedAtUtc.UtcDateTime.ToString("O"),
            format = "jsonl",
            version = 1
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOpt)).ConfigureAwait(false);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            participantCount++;
            var isAdmin = reader.GetInt64(7) == 1;
            var isBot = reader.GetInt64(8) == 1;
            if (isAdmin)
                adminCount++;
            if (isBot)
                botCount++;

            var line = new
            {
                kind = "participant",
                user_id = reader.GetInt64(0),
                user_username = reader.IsDBNull(1) ? null : reader.GetString(1),
                user_first_name = reader.IsDBNull(2) ? null : reader.GetString(2),
                user_last_name = reader.IsDBNull(3) ? null : reader.GetString(3),
                message_count = reader.GetInt64(4),
                first_seen_utc = reader.GetString(5),
                last_seen_utc = reader.GetString(6),
                is_admin = isAdmin,
                is_bot = isBot,
                status = reader.IsDBNull(9) ? null : reader.GetString(9),
                seen_in_messages = reader.GetInt64(10) == 1,
                seen_in_new_chat_members = reader.GetInt64(11) == 1,
                seen_in_chat_member_updates = reader.GetInt64(12) == 1,
                seen_in_admin_snapshot = reader.GetInt64(13) == 1
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOpt)).ConfigureAwait(false);
        }

        var summary = new
        {
            kind = "summary",
            chat_id = chatId,
            participant_count = participantCount,
            admin_count = adminCount,
            bot_count = botCount,
            completed_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOpt)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        _log.LogInformation(
            "Tracked participants export saved: {Path} (chat={ChatId}, participants={ParticipantCount})",
            fullPath,
            chatId,
            participantCount);

        return new TrackedParticipantsExportResult
        {
            FilePath = fullPath,
            ParticipantCount = participantCount
        };
    }

    public async Task<int> CountFlaggedUsersAsync(long? chatId, CancellationToken ct)
    {
        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(DISTINCT user_id)
FROM flagged_events
WHERE user_id IS NOT NULL
  AND ($chat IS NULL OR chat_id = $chat);";
        cmd.Parameters.AddWithValue("$chat", (object?)chatId ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<FlaggedUserEntry>> GetFlaggedUsersPageAsync(
        long? chatId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);

        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH grouped AS (
    SELECT user_id,
           COUNT(1) AS flag_count,
           MAX(flagged_at_utc) AS last_flagged_utc
    FROM flagged_events
    WHERE user_id IS NOT NULL
      AND ($chat IS NULL OR chat_id = $chat)
    GROUP BY user_id
),
last_event AS (
    SELECT user_id, user_username, user_first_name, user_last_name,
           chat_id, chat_title, label, confidence
    FROM (
        SELECT user_id, user_username, user_first_name, user_last_name,
               chat_id, chat_title, label, confidence, flagged_at_utc, id,
               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY flagged_at_utc DESC, id DESC) AS rn
        FROM flagged_events
        WHERE user_id IS NOT NULL
          AND ($chat IS NULL OR chat_id = $chat)
    ) t
    WHERE rn = 1
)
SELECT g.user_id,
       e.user_username,
       e.user_first_name,
       e.user_last_name,
       g.flag_count,
       g.last_flagged_utc,
       e.chat_id,
       e.chat_title,
       e.label,
       e.confidence
FROM grouped g
LEFT JOIN last_event e ON e.user_id = g.user_id
ORDER BY g.last_flagged_utc DESC
LIMIT $lim OFFSET $off;";
        cmd.Parameters.AddWithValue("$chat", (object?)chatId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", pageSize);
        cmd.Parameters.AddWithValue("$off", (page - 1) * pageSize);

        var list = new List<FlaggedUserEntry>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new FlaggedUserEntry
            {
                UserId = reader.GetInt64(0),
                Username = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstName = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastName = reader.IsDBNull(3) ? null : reader.GetString(3),
                FlagCount = reader.GetInt64(4),
                LastFlaggedUtc = DateTimeOffset.Parse(reader.GetString(5)),
                LastChatId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                LastChatTitle = reader.IsDBNull(7) ? null : reader.GetString(7),
                LastLabel = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastConfidence = reader.IsDBNull(9) ? null : reader.GetDouble(9)
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<UserChatSummaryEntry>> GetUserChatsAsync(long userId, CancellationToken ct)
    {
        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.chat_id,
       MAX(m.chat_title) AS chat_title,
       MAX(m.chat_username) AS chat_username,
       COUNT(1) AS message_count,
       MIN(m.timestamp_utc) AS first_seen_utc,
       MAX(m.timestamp_utc) AS last_seen_utc,
       COALESCE((
          SELECT COUNT(1)
          FROM flagged_events f
          WHERE f.user_id = $uid
            AND f.chat_id = m.chat_id
       ), 0) AS flagged_count
FROM messages m
WHERE m.user_id = $uid
GROUP BY m.chat_id
ORDER BY last_seen_utc DESC;";
        cmd.Parameters.AddWithValue("$uid", userId);

        var list = new List<UserChatSummaryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new UserChatSummaryEntry
            {
                ChatId = reader.GetInt64(0),
                ChatTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
                ChatUsername = reader.IsDBNull(2) ? null : reader.GetString(2),
                MessageCount = reader.GetInt64(3),
                FirstSeenUtc = DateTimeOffset.Parse(reader.GetString(4)),
                LastSeenUtc = DateTimeOffset.Parse(reader.GetString(5)),
                FlaggedCount = reader.GetInt64(6)
            });
        }

        return list;
    }

    public async Task<UserMessagesExportResult> ExportUserMessagesAsync(long userId, long? chatId, CancellationToken ct)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var fileName = chatId.HasValue
            ? $"user_{userId}_chat_{chatId.Value}_{startedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz"
            : $"user_{userId}_all_chats_{startedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz";
        var fullPath = Path.Combine(_exportsDir, fileName);

        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.id,
       m.timestamp_utc,
       m.chat_id,
       m.chat_title,
       m.chat_username,
       m.user_id,
       m.user_username,
       m.user_first_name,
       m.user_last_name,
       m.message_id,
       m.text,
       f.label,
       f.confidence,
       f.reason,
       f.flagged_at_utc
FROM messages m
LEFT JOIN flagged_events f
  ON f.chat_id = m.chat_id
 AND f.message_id = m.message_id
 AND (($uid IS NULL AND f.user_id IS NULL) OR f.user_id = $uid)
WHERE m.user_id = $uid
  AND ($chat IS NULL OR m.chat_id = $chat)
ORDER BY m.timestamp_utc ASC, m.id ASC;";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$chat", (object?)chatId ?? DBNull.Value);

        long messageCount = 0;
        var chats = new HashSet<long>();

        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gz, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var header = new
        {
            kind = "meta",
            export_type = "user_messages",
            user_id = userId,
            chat_id = chatId,
            exported_at_utc = startedAtUtc.UtcDateTime.ToString("O"),
            format = "jsonl",
            version = 1
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOpt)).ConfigureAwait(false);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messageCount++;
            var currentChatId = reader.GetInt64(2);
            chats.Add(currentChatId);

            var line = new
            {
                kind = "message",
                store_id = reader.GetInt64(0),
                timestamp_utc = reader.GetString(1),
                chat_id = currentChatId,
                chat_title = reader.IsDBNull(3) ? null : reader.GetString(3),
                chat_username = reader.IsDBNull(4) ? null : reader.GetString(4),
                user_id = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                user_username = reader.IsDBNull(6) ? null : reader.GetString(6),
                user_first_name = reader.IsDBNull(7) ? null : reader.GetString(7),
                user_last_name = reader.IsDBNull(8) ? null : reader.GetString(8),
                message_id = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9),
                text = reader.IsDBNull(10) ? null : reader.GetString(10),
                flagged = reader.IsDBNull(11)
                    ? null
                    : new
                    {
                        label = reader.GetString(11),
                        confidence = reader.IsDBNull(12) ? (double?)null : reader.GetDouble(12),
                        reason = reader.IsDBNull(13) ? null : reader.GetString(13),
                        flagged_at_utc = reader.IsDBNull(14) ? null : reader.GetString(14)
                    }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOpt)).ConfigureAwait(false);
        }

        var summary = new
        {
            kind = "summary",
            user_id = userId,
            chat_id = chatId,
            message_count = messageCount,
            chat_count = chats.Count,
            completed_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOpt)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        _log.LogInformation(
            "User messages export saved: {Path} (user={UserId}, chat={ChatId}, messages={MessageCount}, chats={ChatCount})",
            fullPath,
            userId,
            chatId,
            messageCount,
            chats.Count);

        return new UserMessagesExportResult
        {
            FilePath = fullPath,
            MessageCount = messageCount,
            ChatCount = chats.Count
        };
    }

    private static string BuildParticipantKey(long? userId, string? username, string? firstName, string? lastName)
    {
        if (userId.HasValue)
            return $"id:{userId.Value}";

        if (!string.IsNullOrWhiteSpace(username))
            return $"username:{username.Trim().ToLowerInvariant()}";

        var fullName = $"{firstName} {lastName}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
            return $"name:{fullName}";

        return "unknown";
    }

    private SqliteConnection OpenReadOnly()
    {
        var full = Path.GetFullPath(_dbPath);
        var conn = new SqliteConnection($"Data Source={full};Mode=ReadOnly;Cache=Shared");
        conn.Open();
        return conn;
    }
}
