using Microsoft.Data.Sqlite;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace HarborSentinel.Bot.Services;

public sealed class SqliteMessageStore : IMessageStore
{
    private static readonly Regex WeakFollowUpRegex = new(
        @"^(?:да|да\?|ага|угу|ок|окей|ясно|понял|понятно|знаю|интересно|мм+|хм+|ну да|ты его знаешь\??|его знаешь\??|кто\?|и\?|да\?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongIncidentSignalRegex = new(
        @"(?:monero|легион|jihad|джихад|manua(?:l)?|мануал|обучени|инструкц|pdf|документ|архив|файл|переименуй|сделал новый|номер скинь|фио|sni|домен|симк|симка|vpn|tor|шифр|запрос от другого лица|реквизит|сбор|пожертв)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILogger<SqliteMessageStore> _log;
    private readonly string _dbPath;

    public SqliteMessageStore(ILogger<SqliteMessageStore> log, IOptions<DatabaseOptions> opt)
    {
        _log = log;
        _dbPath = opt.Value.DbPath;
        var full = Path.GetFullPath(_dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    }

    public async Task InitAsync(CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS messages(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  timestamp_utc   TEXT    NOT NULL,
  chat_id         INTEGER NOT NULL,
  chat_title      TEXT,
  chat_username   TEXT,
  user_id         INTEGER,
  user_username   TEXT,
  user_first_name TEXT,
  user_last_name  TEXT,
  message_id      INTEGER,
  text            TEXT
);
CREATE INDEX IF NOT EXISTS ix_messages_chat_user_time
ON messages(chat_id, user_id, timestamp_utc);
CREATE INDEX IF NOT EXISTS ix_messages_chat_time
ON messages(chat_id, timestamp_utc, id);
CREATE INDEX IF NOT EXISTS ix_messages_user_time
ON messages(user_id, timestamp_utc, id);

CREATE TABLE IF NOT EXISTS chat_members(
  chat_id                        INTEGER NOT NULL,
  user_id                        INTEGER NOT NULL,
  username                       TEXT,
  first_name                     TEXT,
  last_name                      TEXT,
  is_bot                         INTEGER NOT NULL DEFAULT 0,
  status                         TEXT,
  is_admin                       INTEGER NOT NULL DEFAULT 0,
  first_seen_utc                 TEXT    NOT NULL,
  last_seen_utc                  TEXT    NOT NULL,
  last_message_utc               TEXT,
  last_membership_event_utc      TEXT,
  last_admin_snapshot_utc        TEXT,
  seen_in_messages               INTEGER NOT NULL DEFAULT 0,
  seen_in_new_chat_members       INTEGER NOT NULL DEFAULT 0,
  seen_in_chat_member_updates    INTEGER NOT NULL DEFAULT 0,
  seen_in_admin_snapshot         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(chat_id, user_id)
);
CREATE INDEX IF NOT EXISTS ix_chat_members_chat_last_seen
ON chat_members(chat_id, last_seen_utc DESC, user_id ASC);
CREATE INDEX IF NOT EXISTS ix_chat_members_chat_admin
ON chat_members(chat_id, is_admin DESC, last_seen_utc DESC, user_id ASC);

CREATE TABLE IF NOT EXISTS private_start_users(
  user_id              INTEGER PRIMARY KEY,
  first_name           TEXT,
  last_name            TEXT,
  username             TEXT,
  phone                TEXT,
  started_at_utc       TEXT    NOT NULL,
  last_started_at_utc  TEXT    NOT NULL,
  start_count          INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_private_start_users_last_started
ON private_start_users(last_started_at_utc DESC, user_id DESC);

CREATE TABLE IF NOT EXISTS flagged_events(
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  flagged_at_utc  TEXT    NOT NULL,
  chat_id         INTEGER NOT NULL,
  chat_title      TEXT,
  chat_username   TEXT,
  user_id         INTEGER,
  user_username   TEXT,
  user_first_name TEXT,
  user_last_name  TEXT,
  message_id      INTEGER,
  label           TEXT    NOT NULL,
  confidence      REAL,
  reason          TEXT
);
CREATE INDEX IF NOT EXISTS ix_flagged_events_user_time
ON flagged_events(user_id, flagged_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_flagged_events_chat_time
ON flagged_events(chat_id, flagged_at_utc DESC);

CREATE TABLE IF NOT EXISTS risk_events(
  id                    INTEGER PRIMARY KEY AUTOINCREMENT,
  event_at_utc          TEXT    NOT NULL,
  chat_id               INTEGER NOT NULL,
  user_id               INTEGER NOT NULL,
  message_id            INTEGER,
  label                 TEXT,
  confidence            REAL,
  topic                 TEXT,
  intent                TEXT,
  needs_context_review  INTEGER NOT NULL DEFAULT 0,
  risk_score            REAL    NOT NULL,
  reason                TEXT
);
CREATE INDEX IF NOT EXISTS ix_risk_events_chat_user_time
ON risk_events(chat_id, user_id, event_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_risk_events_chat_time
ON risk_events(chat_id, event_at_utc DESC);

CREATE TABLE IF NOT EXISTS risk_alert_state(
  chat_id               INTEGER NOT NULL,
  user_id               INTEGER NOT NULL,
  last_alerted_at_utc   TEXT    NOT NULL,
  last_score            REAL    NOT NULL,
  PRIMARY KEY(chat_id, user_id)
);
CREATE TABLE IF NOT EXISTS chat_vigilance_state(
  chat_id                INTEGER PRIMARY KEY,
  heightened_until_utc   TEXT    NOT NULL,
  last_triggered_at_utc  TEXT    NOT NULL,
  trigger_count          INTEGER NOT NULL DEFAULT 1,
  last_label             TEXT,
  last_topic             TEXT,
  last_risk_level        TEXT,
  last_reason            TEXT,
  last_score             REAL
);
CREATE INDEX IF NOT EXISTS ix_chat_vigilance_until
ON chat_vigilance_state(heightened_until_utc DESC);
CREATE TABLE IF NOT EXISTS app_meta(
  key             TEXT PRIMARY KEY,
  value           TEXT NOT NULL
);";
        await cmd.ExecuteNonQueryAsync(ct);
        await EnsureLegacyDedupOnceAsync(c, ct).ConfigureAwait(false);
        await EnsureUniqueIndexesAsync(c, ct).ConfigureAwait(false);
        _log.LogInformation("SQLite ready at {Path}", Path.GetFullPath(_dbPath));
    }

    public async Task AppendAsync(MessageLogRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO messages(timestamp_utc, chat_id, chat_title, chat_username,
                               user_id, user_username, user_first_name, user_last_name,
                               message_id, text)
VALUES($ts,$chat,$ctitle,$cuser,$uid,$uuser,$uf,$ul,$mid,$text);";
        cmd.Parameters.AddWithValue("$ts", r.Timestamp.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$chat", r.ChatId);
        cmd.Parameters.AddWithValue("$ctitle", r.ChatTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$cuser", r.ChatUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", r.FromId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uuser", r.FromUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uf", r.FromFirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ul", r.FromLastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", r.MessageId);
        cmd.Parameters.AddWithValue("$text", r.Text ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertChatMemberAsync(ChatMemberRegistryRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO chat_members(
    chat_id, user_id, username, first_name, last_name, is_bot, status, is_admin,
    first_seen_utc, last_seen_utc, last_message_utc, last_membership_event_utc, last_admin_snapshot_utc,
    seen_in_messages, seen_in_new_chat_members, seen_in_chat_member_updates, seen_in_admin_snapshot)
VALUES(
    $chatId, $userId, $username, $firstName, $lastName, $isBot, $status, $isAdmin,
    $observedAtUtc, $observedAtUtc, $lastMessageUtc, $lastMembershipEventUtc, $lastAdminSnapshotUtc,
    $seenInMessages, $seenInNewChatMembers, $seenInChatMemberUpdates, $seenInAdminSnapshot)
ON CONFLICT(chat_id, user_id) DO UPDATE SET
    username = COALESCE(excluded.username, chat_members.username),
    first_name = COALESCE(excluded.first_name, chat_members.first_name),
    last_name = COALESCE(excluded.last_name, chat_members.last_name),
    is_bot = CASE
        WHEN excluded.is_bot = 1 THEN 1
        ELSE chat_members.is_bot
    END,
    status = CASE
        WHEN excluded.seen_in_new_chat_members = 1 OR excluded.seen_in_chat_member_updates = 1 OR excluded.seen_in_admin_snapshot = 1
            THEN COALESCE(excluded.status, chat_members.status)
        ELSE chat_members.status
    END,
    is_admin = CASE
        WHEN excluded.seen_in_chat_member_updates = 1 OR excluded.seen_in_admin_snapshot = 1
            THEN excluded.is_admin
        ELSE chat_members.is_admin
    END,
    last_seen_utc = CASE
        WHEN excluded.last_seen_utc > chat_members.last_seen_utc
            THEN excluded.last_seen_utc
        ELSE chat_members.last_seen_utc
    END,
    last_message_utc = CASE
        WHEN excluded.last_message_utc IS NULL THEN chat_members.last_message_utc
        WHEN chat_members.last_message_utc IS NULL OR excluded.last_message_utc > chat_members.last_message_utc
            THEN excluded.last_message_utc
        ELSE chat_members.last_message_utc
    END,
    last_membership_event_utc = CASE
        WHEN excluded.last_membership_event_utc IS NULL THEN chat_members.last_membership_event_utc
        WHEN chat_members.last_membership_event_utc IS NULL OR excluded.last_membership_event_utc > chat_members.last_membership_event_utc
            THEN excluded.last_membership_event_utc
        ELSE chat_members.last_membership_event_utc
    END,
    last_admin_snapshot_utc = CASE
        WHEN excluded.last_admin_snapshot_utc IS NULL THEN chat_members.last_admin_snapshot_utc
        WHEN chat_members.last_admin_snapshot_utc IS NULL OR excluded.last_admin_snapshot_utc > chat_members.last_admin_snapshot_utc
            THEN excluded.last_admin_snapshot_utc
        ELSE chat_members.last_admin_snapshot_utc
    END,
    seen_in_messages = CASE
        WHEN excluded.seen_in_messages = 1 THEN 1
        ELSE chat_members.seen_in_messages
    END,
    seen_in_new_chat_members = CASE
        WHEN excluded.seen_in_new_chat_members = 1 THEN 1
        ELSE chat_members.seen_in_new_chat_members
    END,
    seen_in_chat_member_updates = CASE
        WHEN excluded.seen_in_chat_member_updates = 1 THEN 1
        ELSE chat_members.seen_in_chat_member_updates
    END,
    seen_in_admin_snapshot = CASE
        WHEN excluded.seen_in_admin_snapshot = 1 THEN 1
        ELSE chat_members.seen_in_admin_snapshot
    END;";
        cmd.Parameters.AddWithValue("$chatId", r.ChatId);
        cmd.Parameters.AddWithValue("$userId", r.UserId);
        cmd.Parameters.AddWithValue("$username", r.Username ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$firstName", r.FirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lastName", r.LastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$isBot", r.IsBot ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", r.Status ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$isAdmin", r.IsAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$observedAtUtc", r.ObservedAtUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue(
            "$lastMessageUtc",
            r.SeenInMessages ? r.ObservedAtUtc.UtcDateTime.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$lastMembershipEventUtc",
            (r.SeenInNewChatMembers || r.SeenInChatMemberUpdates)
                ? r.ObservedAtUtc.UtcDateTime.ToString("O")
                : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$lastAdminSnapshotUtc",
            r.SeenInAdminSnapshot ? r.ObservedAtUtc.UtcDateTime.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$seenInMessages", r.SeenInMessages ? 1 : 0);
        cmd.Parameters.AddWithValue("$seenInNewChatMembers", r.SeenInNewChatMembers ? 1 : 0);
        cmd.Parameters.AddWithValue("$seenInChatMemberUpdates", r.SeenInChatMemberUpdates ? 1 : 0);
        cmd.Parameters.AddWithValue("$seenInAdminSnapshot", r.SeenInAdminSnapshot ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RegisterPrivateStartAsync(MessageLogRecord r, string? phone, CancellationToken ct)
    {
        if (!r.FromId.HasValue)
            return;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO private_start_users(
    user_id, first_name, last_name, username, phone, started_at_utc, last_started_at_utc, start_count)
VALUES(
    $uid, $firstName, $lastName, $username, $phone, $startedAtUtc, $lastStartedAtUtc, 1)
ON CONFLICT(user_id) DO UPDATE SET
    first_name = excluded.first_name,
    last_name = excluded.last_name,
    username = excluded.username,
    phone = COALESCE(excluded.phone, private_start_users.phone),
    last_started_at_utc = excluded.last_started_at_utc,
    start_count = private_start_users.start_count + 1;";
        cmd.Parameters.AddWithValue("$uid", r.FromId.Value);
        cmd.Parameters.AddWithValue("$firstName", r.FromFirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lastName", r.FromLastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$username", r.FromUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$phone", phone ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAtUtc", r.Timestamp.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$lastStartedAtUtc", r.Timestamp.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdatePrivateStartProfileAsync(MessageLogRecord r, string? phone, CancellationToken ct)
    {
        if (!r.FromId.HasValue)
            return;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE private_start_users
SET first_name = $firstName,
    last_name = $lastName,
    username = $username,
    phone = COALESCE($phone, phone)
WHERE user_id = $uid;";
        cmd.Parameters.AddWithValue("$uid", r.FromId.Value);
        cmd.Parameters.AddWithValue("$firstName", r.FromFirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lastName", r.FromLastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$username", r.FromUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$phone", phone ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountPrivateStartUsersAsync(CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM private_start_users;";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is long longCount ? checked((int)longCount) : Convert.ToInt32(scalar ?? 0);
    }

    public async Task<IReadOnlyList<PrivateStartUserRecord>> GetPrivateStartUsersPageAsync(
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Max(1, pageSize);
        var offset = (normalizedPage - 1) * normalizedPageSize;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT user_id, first_name, last_name, username, phone, started_at_utc, last_started_at_utc, start_count
FROM private_start_users
ORDER BY last_started_at_utc DESC, user_id DESC
LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", normalizedPageSize);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<PrivateStartUserRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadPrivateStartUser(reader));

        return list;
    }

    public async Task<IReadOnlyList<PrivateStartUserRecord>> GetPrivateStartUsersAsync(CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT user_id, first_name, last_name, username, phone, started_at_utc, last_started_at_utc, start_count
FROM private_start_users
ORDER BY last_started_at_utc DESC, user_id DESC;";

        var list = new List<PrivateStartUserRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadPrivateStartUser(reader));

        return list;
    }

    public async Task<AdminHealthSnapshot> GetAdminHealthSnapshotAsync(TimeSpan recentWindow, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sinceUtc = nowUtc - recentWindow;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT
  (SELECT COUNT(1) FROM messages),
  (SELECT MAX(timestamp_utc) FROM messages),
  (SELECT COUNT(1) FROM chat_registry WHERE is_listed = 1),
  (SELECT COUNT(1) FROM chat_registry WHERE is_listed = 1 AND is_active = 1),
  (SELECT COUNT(1) FROM chat_registry WHERE is_listed = 1 AND is_active = 0),
  (SELECT COUNT(1) FROM chat_registry WHERE is_listed = 1 AND is_tracked = 1),
  (SELECT COUNT(1) FROM private_start_users),
  (SELECT COUNT(1) FROM flagged_events),
  (SELECT COUNT(1) FROM flagged_events WHERE flagged_at_utc >= $since),
  (SELECT MAX(flagged_at_utc) FROM flagged_events),
  (SELECT COUNT(1) FROM risk_events),
  (SELECT COUNT(1) FROM risk_events WHERE event_at_utc >= $since),
  (SELECT MAX(event_at_utc) FROM risk_events),
  (SELECT COUNT(1) FROM chat_vigilance_state WHERE heightened_until_utc >= $now);";
        cmd.Parameters.AddWithValue("$since", sinceUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now", nowUtc.UtcDateTime.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        return new AdminHealthSnapshot
        {
            TotalMessages = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            LastMessageUtc = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
            TotalChats = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            ActiveChats = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            RemovedChats = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            TrackedChats = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            PrivateStartUsers = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            TotalFlaggedEvents = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            RecentFlaggedEvents = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
            LastFlaggedUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            TotalRiskEvents = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
            RecentRiskEvents = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
            LastRiskUtc = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            ActiveVigilanceChats = reader.IsDBNull(13) ? 0 : reader.GetInt32(13)
        };
    }

    public async Task<ChatModerationSummary> GetChatModerationSummaryAsync(
        long chatId,
        TimeSpan recentWindow,
        CancellationToken ct)
    {
        var sinceUtc = DateTimeOffset.UtcNow - recentWindow;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT
  (SELECT COUNT(1) FROM messages WHERE chat_id = $chat),
  (SELECT MAX(timestamp_utc) FROM messages WHERE chat_id = $chat),
  (SELECT COUNT(1) FROM (
      SELECT user_id FROM chat_members WHERE chat_id = $chat
      UNION
      SELECT user_id FROM messages WHERE chat_id = $chat AND user_id IS NOT NULL
  )),
  (SELECT COUNT(1) FROM (
      SELECT DISTINCT user_id FROM flagged_events WHERE chat_id = $chat AND user_id IS NOT NULL
  )),
  (SELECT COUNT(1) FROM flagged_events WHERE chat_id = $chat),
  (SELECT COUNT(1) FROM flagged_events WHERE chat_id = $chat AND flagged_at_utc >= $since),
  (SELECT MAX(flagged_at_utc) FROM flagged_events WHERE chat_id = $chat),
  (SELECT COUNT(1) FROM risk_events WHERE chat_id = $chat),
  (SELECT COUNT(1) FROM risk_events WHERE chat_id = $chat AND event_at_utc >= $since),
  (SELECT MAX(event_at_utc) FROM risk_events WHERE chat_id = $chat),
  (SELECT label FROM risk_events WHERE chat_id = $chat ORDER BY event_at_utc DESC, id DESC LIMIT 1),
  (SELECT topic FROM risk_events WHERE chat_id = $chat ORDER BY event_at_utc DESC, id DESC LIMIT 1),
  (SELECT intent FROM risk_events WHERE chat_id = $chat ORDER BY event_at_utc DESC, id DESC LIMIT 1),
  (SELECT reason FROM risk_events WHERE chat_id = $chat ORDER BY event_at_utc DESC, id DESC LIMIT 1),
  (SELECT risk_score FROM risk_events WHERE chat_id = $chat ORDER BY event_at_utc DESC, id DESC LIMIT 1);";
        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$since", sinceUtc.UtcDateTime.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        return new ChatModerationSummary
        {
            ChatId = chatId,
            MessageCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            LastMessageUtc = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
            ParticipantCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            FlaggedUserCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            FlaggedEventCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
            RecentFlaggedEventCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            LastFlaggedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            RiskEventCount = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            RecentRiskEventCount = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
            LastRiskUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            LastRiskLabel = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastRiskTopic = reader.IsDBNull(11) ? null : reader.GetString(11),
            LastRiskIntent = reader.IsDBNull(12) ? null : reader.GetString(12),
            LastRiskReason = reader.IsDBNull(13) ? null : reader.GetString(13),
            LastRiskScore = reader.IsDBNull(14) ? null : reader.GetDouble(14)
        };
    }

    public async Task<int> CountIncidentFeedAsync(long? chatId, TimeSpan recentWindow, CancellationToken ct)
    {
        var feed = await GetIncidentFeedAggregatedAsync(chatId, recentWindow, ct).ConfigureAwait(false);
        return feed.Count;
    }

    public async Task<IReadOnlyList<IncidentFeedEntry>> GetIncidentFeedPageAsync(
        long? chatId,
        int page,
        int pageSize,
        TimeSpan recentWindow,
        CancellationToken ct)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Max(1, pageSize);
        var offset = (normalizedPage - 1) * normalizedPageSize;
        var feed = await GetIncidentFeedAggregatedAsync(chatId, recentWindow, ct).ConfigureAwait(false);
        return feed.Skip(offset).Take(normalizedPageSize).ToArray();
    }

    private async Task<IReadOnlyList<IncidentFeedEntry>> GetIncidentFeedAggregatedAsync(
        long? chatId,
        TimeSpan recentWindow,
        CancellationToken ct)
    {
        var rawEntries = await GetIncidentFeedCandidatesAsync(chatId, recentWindow, ct).ConfigureAwait(false);
        if (rawEntries.Count == 0)
            return rawEntries;

        var filtered = rawEntries
            .Where(ShouldExposeIncidentEntry)
            .ToArray();

        if (filtered.Length == 0)
            return Array.Empty<IncidentFeedEntry>();

        var grouped = filtered
            .GroupBy(BuildIncidentGroupKey)
            .Select(static group =>
            {
                var representative = group
                    .OrderByDescending(GetIncidentPriorityScore)
                    .ThenByDescending(static item => item.EventAtUtc)
                    .First();

                return new IncidentFeedEntry
                {
                    EventAtUtc = representative.EventAtUtc,
                    ChatId = representative.ChatId,
                    ChatTitle = representative.ChatTitle,
                    UserId = representative.UserId,
                    Username = representative.Username,
                    FirstName = representative.FirstName,
                    LastName = representative.LastName,
                    MessageId = representative.MessageId,
                    Label = representative.Label,
                    Topic = representative.Topic,
                    Intent = representative.Intent,
                    Confidence = representative.Confidence,
                    RiskScore = representative.RiskScore,
                    NeedsContextReview = representative.NeedsContextReview,
                    MessageText = representative.MessageText,
                    GroupedCount = group.Count(),
                    Reason = CleanIncidentReason(representative.Reason)
                };
            })
            .OrderByDescending(static item => item.EventAtUtc)
            .ThenByDescending(static item => item.RiskScore)
            .ToArray();

        return grouped;
    }

    private async Task<IReadOnlyList<IncidentFeedEntry>> GetIncidentFeedCandidatesAsync(
        long? chatId,
        TimeSpan recentWindow,
        CancellationToken ct)
    {
        var sinceUtc = DateTimeOffset.UtcNow - recentWindow;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
WITH latest_user_profile AS (
    SELECT chat_id, user_id, user_username, user_first_name, user_last_name
    FROM (
        SELECT chat_id,
               user_id,
               user_username,
               user_first_name,
               user_last_name,
               ROW_NUMBER() OVER (PARTITION BY chat_id, user_id ORDER BY timestamp_utc DESC, id DESC) AS rn
        FROM messages
        WHERE user_id IS NOT NULL
    ) t
    WHERE rn = 1
)
SELECT r.event_at_utc,
       r.chat_id,
       COALESCE(m.chat_title, cr.chat_title) AS chat_title,
       r.user_id,
       COALESCE(m.user_username, lup.user_username) AS username,
       COALESCE(m.user_first_name, lup.user_first_name) AS first_name,
       COALESCE(m.user_last_name, lup.user_last_name) AS last_name,
       r.message_id,
       r.label,
       r.topic,
       r.intent,
       r.confidence,
       r.risk_score,
       r.needs_context_review,
       m.text,
       r.reason
FROM risk_events r
LEFT JOIN messages m
       ON m.chat_id = r.chat_id
      AND m.message_id = r.message_id
LEFT JOIN chat_registry cr
       ON cr.chat_id = r.chat_id
LEFT JOIN latest_user_profile lup
       ON lup.chat_id = r.chat_id
      AND lup.user_id = r.user_id
WHERE ($chat IS NULL OR r.chat_id = $chat)
  AND r.event_at_utc >= $since
  AND (
      r.risk_score >= 1.0
      OR COALESCE(r.label, '') NOT IN ('neutral', 'unsure')
      OR r.needs_context_review = 1
  )
ORDER BY r.event_at_utc DESC, r.id DESC;";
        cmd.Parameters.AddWithValue("$chat", (object?)chatId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$since", sinceUtc.UtcDateTime.ToString("O"));

        var list = new List<IncidentFeedEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new IncidentFeedEntry
            {
                EventAtUtc = DateTimeOffset.Parse(reader.GetString(0)),
                ChatId = reader.GetInt64(1),
                ChatTitle = reader.IsDBNull(2) ? null : reader.GetString(2),
                UserId = reader.GetInt64(3),
                Username = reader.IsDBNull(4) ? null : reader.GetString(4),
                FirstName = reader.IsDBNull(5) ? null : reader.GetString(5),
                LastName = reader.IsDBNull(6) ? null : reader.GetString(6),
                MessageId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Label = reader.IsDBNull(8) ? null : reader.GetString(8),
                Topic = reader.IsDBNull(9) ? null : reader.GetString(9),
                Intent = reader.IsDBNull(10) ? null : reader.GetString(10),
                Confidence = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                RiskScore = reader.IsDBNull(12) ? 0d : reader.GetDouble(12),
                NeedsContextReview = !reader.IsDBNull(13) && reader.GetInt64(13) == 1,
                MessageText = reader.IsDBNull(14) ? null : reader.GetString(14),
                Reason = reader.IsDBNull(15) ? null : reader.GetString(15)
            });
        }

        return list;
    }

    private static bool ShouldExposeIncidentEntry(IncidentFeedEntry entry)
    {
        var label = entry.Label?.Trim().ToLowerInvariant();
        var confidence = entry.Confidence ?? 0d;
        var text = entry.MessageText;
        var weakFollowUp = IsWeakFollowUpText(text);
        var hasStrongSignals = HasStrongIncidentSignals(text) || HasStrongIncidentSignals(entry.Reason);

        if (label is "neutral")
            return false;

        if (label is "unsure")
            return entry.RiskScore >= 7.5d || (entry.NeedsContextReview && hasStrongSignals);

        if (weakFollowUp && !hasStrongSignals)
            return false;

        if (entry.NeedsContextReview && confidence <= 0.45d && entry.RiskScore < 8.5d && !hasStrongSignals)
            return false;

        return entry.RiskScore >= 6.0d || confidence >= 0.7d || hasStrongSignals;
    }

    private static string BuildIncidentGroupKey(IncidentFeedEntry entry)
    {
        const int bucketMinutes = 15;
        var primaryKind = !string.IsNullOrWhiteSpace(entry.Topic) && !string.Equals(entry.Topic, "unknown", StringComparison.OrdinalIgnoreCase)
            ? entry.Topic!.Trim().ToLowerInvariant()
            : (entry.Label ?? "other").Trim().ToLowerInvariant();

        var utc = entry.EventAtUtc.UtcDateTime;
        var minutesBucket = (utc.Minute / bucketMinutes) * bucketMinutes;
        var bucket = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, minutesBucket, 0, TimeSpan.Zero);
        return $"{entry.ChatId}:{entry.UserId}:{primaryKind}:{bucket:O}";
    }

    private static double GetIncidentPriorityScore(IncidentFeedEntry entry)
    {
        var score = entry.RiskScore;
        if (!string.IsNullOrWhiteSpace(entry.Label) &&
            !string.Equals(entry.Label, "neutral", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Label, "unsure", StringComparison.OrdinalIgnoreCase))
            score += 4d;

        if (entry.Confidence.HasValue)
            score += Math.Clamp(entry.Confidence.Value, 0d, 1d);

        if (HasStrongIncidentSignals(entry.MessageText))
            score += 3d;

        if (entry.NeedsContextReview)
            score += 0.5d;

        if (IsWeakFollowUpText(entry.MessageText))
            score -= 3d;

        return score;
    }

    private static bool IsWeakFollowUpText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        if (WeakFollowUpRegex.IsMatch(normalized))
            return true;

        return normalized.Length <= 18 &&
               normalized.Count(static ch => !char.IsWhiteSpace(ch)) <= 16 &&
               !normalized.Contains('\n');
    }

    private static bool HasStrongIncidentSignals(string? text)
        => !string.IsNullOrWhiteSpace(text) && StrongIncidentSignalRegex.IsMatch(text);

    private static string? CleanIncidentReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var cleaned = reason
            .Replace("В контексте heightened_vigilance и ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("В контексте heightened_vigilance ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("В режиме heightened_vigilance ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("heightened_vigilance", "контекст", StringComparison.OrdinalIgnoreCase)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        return cleaned;
    }

    public async Task RecordFlaggedEventAsync(MessageLogRecord r, CancellationToken ct)
    {
        if (r.Ai?.Flagged != true)
            return;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO flagged_events(
    flagged_at_utc, chat_id, chat_title, chat_username,
    user_id, user_username, user_first_name, user_last_name,
    message_id, label, confidence, reason)
VALUES(
    $ts, $chat, $ctitle, $cuser,
    $uid, $uuser, $uf, $ul,
    $mid, $label, $confidence, $reason);";
        cmd.Parameters.AddWithValue("$ts", r.Timestamp.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$chat", r.ChatId);
        cmd.Parameters.AddWithValue("$ctitle", r.ChatTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$cuser", r.ChatUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", r.FromId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uuser", r.FromUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uf", r.FromFirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ul", r.FromLastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", r.MessageId);
        cmd.Parameters.AddWithValue("$label", r.Ai?.Label ?? "unsure");
        cmd.Parameters.AddWithValue("$confidence", (object?)r.Ai?.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", r.Ai?.Reason ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordRiskEventAsync(MessageLogRecord r, double riskScore, CancellationToken ct)
    {
        if (!r.FromId.HasValue || riskScore <= 0d)
            return;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO risk_events(
    event_at_utc, chat_id, user_id, message_id,
    label, confidence, topic, intent, needs_context_review, risk_score, reason)
VALUES(
    $ts, $chat, $uid, $mid,
    $label, $confidence, $topic, $intent, $needsContextReview, $riskScore, $reason);";
        cmd.Parameters.AddWithValue("$ts", r.Timestamp.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$chat", r.ChatId);
        cmd.Parameters.AddWithValue("$uid", r.FromId.Value);
        cmd.Parameters.AddWithValue("$mid", r.MessageId);
        cmd.Parameters.AddWithValue("$label", r.Ai?.Label ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$confidence", (object?)r.Ai?.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$topic", r.Ai?.Topic ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$intent", r.Ai?.Intent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$needsContextReview", r.Ai?.NeedsContextReview == true ? 1 : 0);
        cmd.Parameters.AddWithValue("$riskScore", riskScore);
        cmd.Parameters.AddWithValue("$reason", r.Ai?.Reason ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ConversationRiskSnapshot?> GetConversationRiskSnapshotAsync(
        long chatId,
        long? userId,
        TimeSpan window,
        CancellationToken ct)
    {
        var sinceUtc = DateTimeOffset.UtcNow - window;
        var scopeUserId = userId ?? 0L;

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT
    COALESCE(SUM(risk_score), 0),
    COUNT(*),
    MAX(event_at_utc),
    COUNT(DISTINCT user_id),
    (
        SELECT last_alerted_at_utc
        FROM risk_alert_state
        WHERE chat_id = $chat AND user_id = $scopeUid
    )
FROM risk_events
WHERE chat_id = $chat
  AND ($uid IS NULL OR user_id = $uid)
  AND event_at_utc >= $since;";
        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scopeUid", scopeUserId);
        cmd.Parameters.AddWithValue("$since", sinceUtc.UtcDateTime.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var totalScore = reader.IsDBNull(0) ? 0d : reader.GetDouble(0);
        var eventCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        if (eventCount <= 0 || totalScore <= 0d)
            return null;

        return new ConversationRiskSnapshot
        {
            ChatId = chatId,
            UserId = userId,
            TotalScore = totalScore,
            EventCount = eventCount,
            ParticipantCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            LastEventUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
            LastAlertedAtUtc = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4))
        };
    }

    public async Task<bool> TryMarkConversationRiskAlertAsync(
        long chatId,
        long? userId,
        DateTimeOffset nowUtc,
        double totalScore,
        TimeSpan cooldown,
        CancellationToken ct)
    {
        var scopeUserId = userId ?? 0L;
        await using var c = Open();
        await using var transaction = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        DateTimeOffset? lastAlertedAtUtc = null;
        await using (var selectCmd = c.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = @"
SELECT last_alerted_at_utc
FROM risk_alert_state
WHERE chat_id = $chat AND user_id = $uid
LIMIT 1;";
            selectCmd.Parameters.AddWithValue("$chat", chatId);
            selectCmd.Parameters.AddWithValue("$uid", scopeUserId);
            var scalar = await selectCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string value && !string.IsNullOrWhiteSpace(value))
                lastAlertedAtUtc = DateTimeOffset.Parse(value);
        }

        if (lastAlertedAtUtc.HasValue && lastAlertedAtUtc.Value + cooldown > nowUtc)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await using (var upsertCmd = c.CreateCommand())
        {
            upsertCmd.Transaction = transaction;
            upsertCmd.CommandText = @"
INSERT INTO risk_alert_state(chat_id, user_id, last_alerted_at_utc, last_score)
VALUES($chat, $uid, $ts, $score)
ON CONFLICT(chat_id, user_id)
DO UPDATE SET
    last_alerted_at_utc = excluded.last_alerted_at_utc,
    last_score = excluded.last_score;";
            upsertCmd.Parameters.AddWithValue("$chat", chatId);
            upsertCmd.Parameters.AddWithValue("$uid", scopeUserId);
            upsertCmd.Parameters.AddWithValue("$ts", nowUtc.UtcDateTime.ToString("O"));
            upsertCmd.Parameters.AddWithValue("$score", totalScore);
            await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ChatVigilanceState?> GetChatVigilanceStateAsync(long chatId, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT chat_id,
       heightened_until_utc,
       last_triggered_at_utc,
       trigger_count,
       last_label,
       last_topic,
       last_risk_level,
       last_reason,
       last_score
FROM chat_vigilance_state
WHERE chat_id = $chat
LIMIT 1;";
        cmd.Parameters.AddWithValue("$chat", chatId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ChatVigilanceState
        {
            ChatId = reader.GetInt64(0),
            HeightenedUntilUtc = DateTimeOffset.Parse(reader.GetString(1)),
            LastTriggeredAtUtc = DateTimeOffset.Parse(reader.GetString(2)),
            TriggerCount = reader.GetInt32(3),
            LastLabel = reader.IsDBNull(4) ? null : reader.GetString(4),
            LastTopic = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastRiskLevel = reader.IsDBNull(6) ? null : reader.GetString(6),
            LastReason = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastScore = reader.IsDBNull(8) ? null : reader.GetDouble(8)
        };
    }

    public async Task UpsertChatVigilanceAsync(
        long chatId,
        DateTimeOffset nowUtc,
        DateTimeOffset heightenedUntilUtc,
        string? label,
        string? topic,
        string? riskLevel,
        string? reason,
        double? score,
        CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO chat_vigilance_state(
    chat_id, heightened_until_utc, last_triggered_at_utc, trigger_count,
    last_label, last_topic, last_risk_level, last_reason, last_score)
VALUES(
    $chat, $until, $triggeredAt, 1,
    $label, $topic, $riskLevel, $reason, $score)
ON CONFLICT(chat_id)
DO UPDATE SET
    heightened_until_utc = CASE
        WHEN chat_vigilance_state.heightened_until_utc > excluded.heightened_until_utc
            THEN chat_vigilance_state.heightened_until_utc
        ELSE excluded.heightened_until_utc
    END,
    last_triggered_at_utc = excluded.last_triggered_at_utc,
    trigger_count = chat_vigilance_state.trigger_count + 1,
    last_label = excluded.last_label,
    last_topic = excluded.last_topic,
    last_risk_level = excluded.last_risk_level,
    last_reason = excluded.last_reason,
    last_score = excluded.last_score;";
        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$until", heightenedUntilUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$triggeredAt", nowUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$label", label ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$topic", topic ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$riskLevel", riskLevel ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", reason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DialogMessage>> QueryUserMessagesAsync(
        long chatId, long? userId, DateTimeOffset? sinceUtc, DateTimeOffset? untilUtc,
        int maxCount, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT timestamp_utc, chat_id, chat_title, user_id, user_username, message_id, text
FROM messages
WHERE chat_id = $chat
  AND (($uid IS NULL AND user_id IS NULL) OR user_id = $uid)
  AND ($since IS NULL OR timestamp_utc >= $since)
  AND ($until IS NULL OR timestamp_utc <  $until)
ORDER BY timestamp_utc ASC
LIMIT $lim;";
        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$since", sinceUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$until", untilUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", Math.Max(1, maxCount));

        var list = new List<DialogMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ts = DateTimeOffset.Parse(r.GetString(0));
            var chat = r.GetInt64(1);
            var title = r.IsDBNull(2) ? null : r.GetString(2);
            long? uid = r.IsDBNull(3) ? null : r.GetInt64(3);
            var un = r.IsDBNull(4) ? null : r.GetString(4);
            var mid = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            var text = r.IsDBNull(6) ? null : r.GetString(6);
            list.Add(new DialogMessage
            {
                Timestamp = ts,
                ChatId = chat,
                ChatTitle = title,
                MessageId = mid,
                UserId = uid,
                Username = un,
                Text = text
            });
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var full = Path.GetFullPath(_dbPath);
        var conn = new SqliteConnection($"Data Source={full};Cache=Shared");
        conn.Open();
        return conn;
    }

    private async Task EnsureLegacyDedupOnceAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string markerKey = "dedup_v1_completed";

        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT value FROM app_meta WHERE key = $key LIMIT 1;";
            checkCmd.Parameters.AddWithValue("$key", markerKey);
            var marker = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (marker is string value && string.Equals(value, "1", StringComparison.Ordinal))
                return;
        }

        using var tx = connection.BeginTransaction();

        await using (var dedupCmd = connection.CreateCommand())
        {
            dedupCmd.Transaction = tx;
            dedupCmd.CommandText = @"
DELETE FROM messages
WHERE message_id IS NOT NULL
  AND id NOT IN (
    SELECT MIN(id)
    FROM messages
    WHERE message_id IS NOT NULL
    GROUP BY chat_id, message_id
  );
DELETE FROM flagged_events
WHERE message_id IS NOT NULL
  AND id NOT IN (
    SELECT MIN(id)
    FROM flagged_events
    WHERE message_id IS NOT NULL
    GROUP BY chat_id, message_id
  );";
            await dedupCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var markerCmd = connection.CreateCommand())
        {
            markerCmd.Transaction = tx;
            markerCmd.CommandText = @"
INSERT INTO app_meta(key, value)
VALUES($key, '1')
ON CONFLICT(key) DO UPDATE SET value = '1';";
            markerCmd.Parameters.AddWithValue("$key", markerKey);
            await markerCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        tx.Commit();
    }

    private static PrivateStartUserRecord ReadPrivateStartUser(SqliteDataReader reader)
    {
        return new PrivateStartUserRecord
        {
            UserId = reader.GetInt64(0),
            FirstName = reader.IsDBNull(1) ? null : reader.GetString(1),
            LastName = reader.IsDBNull(2) ? null : reader.GetString(2),
            Username = reader.IsDBNull(3) ? null : reader.GetString(3),
            Phone = reader.IsDBNull(4) ? null : reader.GetString(4),
            StartedAtUtc = DateTimeOffset.Parse(reader.GetString(5)),
            LastStartedAtUtc = DateTimeOffset.Parse(reader.GetString(6)),
            StartCount = reader.GetInt32(7)
        };
    }

    private static async Task EnsureUniqueIndexesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_chat_message
ON messages(chat_id, message_id)
WHERE message_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_flagged_events_chat_message
ON flagged_events(chat_id, message_id)
WHERE message_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_risk_events_chat_message
ON risk_events(chat_id, message_id)
WHERE message_id IS NOT NULL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
