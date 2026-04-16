using HarborSentinel.AdminContracts.Incidents;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HarborSentinel.AdminHost.Services;

public sealed class SqliteIncidentQueryService : IIncidentQueryService
{
    private static readonly Regex WeakFollowUpRegex = new(
        @"^(?:да|да\?|ага|угу|ок|окей|ясно|понял|понятно|знаю|интересно|мм+|хм+|ну да|ты его знаешь\??|его знаешь\??|кто\?|и\?|да\?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongIncidentSignalRegex = new(
        @"(?:monero|легион|jihad|джихад|manua(?:l)?|мануал|обучени|инструкц|pdf|документ|архив|файл|переименуй|сделал новый|номер скинь|фио|sni|домен|симк|симка|vpn|tor|шифр|запрос от другого лица|реквизит|сбор|пожертв)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly ILogger<SqliteIncidentQueryService> _log;

    public SqliteIncidentQueryService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        ILogger<SqliteIncidentQueryService> log)
    {
        _runtimeOptions = runtimeOptions.Value;
        _log = log;
    }

    public async Task<AdminIncidentFeedResponseDto> GetIncidentFeedAsync(
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct)
    {
        var snapshot = await GetIncidentSnapshotAsync(chatId, recentWindowDays, ct).ConfigureAwait(false);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var items = snapshot.Items
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new AdminIncidentFeedResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = snapshot.Items.Count,
            ReturnedCount = items.Length,
            ChatIdFilter = chatId,
            RecentWindowDays = snapshot.RecentWindowDays,
            Summary = snapshot.Summary,
            Items = items,
            Warnings = snapshot.Warnings
        };
    }

    public async Task<IncidentFeedSnapshot> GetIncidentSnapshotAsync(
        long? chatId,
        int recentWindowDays,
        CancellationToken ct)
    {
        recentWindowDays = Math.Clamp(recentWindowDays, 1, 90);

        var warnings = new List<string>();
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        if (!File.Exists(databasePath))
        {
            warnings.Add("Файл runtime-базы данных не найден. Лента инцидентов недоступна.");
            return CreateEmptySnapshot(chatId, recentWindowDays, warnings);
        }

        try
        {
            await using var connection = OpenReadOnly(databasePath);

            if (!await TableExistsAsync(connection, "risk_events", ct).ConfigureAwait(false))
            {
                warnings.Add("Таблица risk_events отсутствует. Лента инцидентов недоступна.");
                return CreateEmptySnapshot(chatId, recentWindowDays, warnings);
            }

            var hasMessages = await TableExistsAsync(connection, "messages", ct).ConfigureAwait(false);
            var hasChatRegistry = await TableExistsAsync(connection, "chat_registry", ct).ConfigureAwait(false);
            var hasListed = hasChatRegistry && await ColumnExistsAsync(connection, "chat_registry", "is_listed", ct).ConfigureAwait(false);
            var hasActive = hasChatRegistry && await ColumnExistsAsync(connection, "chat_registry", "is_active", ct).ConfigureAwait(false);

            if (!hasMessages)
                warnings.Add("Таблица messages отсутствует. Текст и часть профилей в ленте инцидентов могут быть недоступны.");

            if (!hasChatRegistry)
                warnings.Add("Таблица chat_registry отсутствует. Названия чатов и область текущего бота работают в деградированном режиме.");
            else if (!hasListed)
                warnings.Add("Обнаружена старая схема chat_registry: изоляция инцидентов по текущему боту недоступна.");

            var feed = await GetIncidentFeedAggregatedAsync(connection, chatId, recentWindowDays, hasMessages, hasChatRegistry, hasListed, hasActive, ct).ConfigureAwait(false);
            var currentScopeCount = hasListed ? feed.Count(static item => item.InCurrentBotScope is true) : 0;
            var historicalOnlyCount = hasListed ? feed.Count(static item => item.InCurrentBotScope is false) : 0;

            if (hasListed && historicalOnlyCount > 0)
                warnings.Add("В ленту включены исторические инциденты вне текущей области чатов, чтобы не терять review-контекст.");

            var summary = new AdminIncidentFeedSummaryDto
            {
                DistinctChats = feed.Select(static item => item.ChatId).Distinct().Count(),
                DistinctUsers = feed.Select(static item => item.UserId).Distinct().Count(),
                ContextReviewCount = feed.Count(static item => item.NeedsContextReview),
                HighestRiskScore = feed.Count == 0 ? null : feed.Max(static item => item.RiskScore),
                CurrentScopeIncidents = currentScopeCount,
                HistoricalOnlyIncidents = historicalOnlyCount,
                CurrentBotScopeApplied = hasListed,
                HistoricalVisibilityEnabled = hasListed,
                LegacySchemaMode = !hasListed,
                UnreadCount = 0,
                AcknowledgedCount = 0,
                RequiresAttentionCount = 0
            };

            return new IncidentFeedSnapshot(feed, summary, warnings, chatId, recentWindowDays);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to query incident feed from runtime database {Path}", databasePath);
            warnings.Add("Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost.");
            return CreateEmptySnapshot(chatId, recentWindowDays, warnings);
        }
    }

    private static async Task<IReadOnlyList<AdminIncidentFeedItemDto>> GetIncidentFeedAggregatedAsync(
        SqliteConnection connection,
        long? chatId,
        int recentWindowDays,
        bool hasMessages,
        bool hasChatRegistry,
        bool hasListed,
        bool hasActive,
        CancellationToken ct)
    {
        var rawEntries = await GetIncidentFeedCandidatesAsync(connection, chatId, recentWindowDays, hasMessages, hasChatRegistry, hasListed, hasActive, ct).ConfigureAwait(false);
        if (rawEntries.Count == 0)
            return rawEntries;

        var filtered = rawEntries
            .Where(ShouldExposeIncidentEntry)
            .ToArray();

        if (filtered.Length == 0)
            return Array.Empty<AdminIncidentFeedItemDto>();

        var grouped = filtered
            .GroupBy(BuildIncidentGroupKey)
            .Select(static group =>
            {
                var representative = group
                    .OrderByDescending(GetIncidentPriorityScore)
                    .ThenByDescending(static item => item.EventAtUtc)
                    .First();

                return new AdminIncidentFeedItemDto
                {
                    IncidentId = BuildIncidentId(representative),
                    EventAtUtc = representative.EventAtUtc,
                    ChatId = representative.ChatId,
                    ChatTitle = representative.ChatTitle,
                    InCurrentBotScope = representative.InCurrentBotScope,
                    ChatIsActive = representative.ChatIsActive,
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
                    Reason = CleanIncidentReason(representative.Reason),
                    IsUnread = false,
                    IsAcknowledged = false
                };
            })
            .OrderByDescending(static item => item.EventAtUtc)
            .ThenByDescending(static item => item.RiskScore)
            .ToArray();

        return grouped;
    }

    private static async Task<IReadOnlyList<AdminIncidentFeedItemDto>> GetIncidentFeedCandidatesAsync(
        SqliteConnection connection,
        long? chatId,
        int recentWindowDays,
        bool hasMessages,
        bool hasChatRegistry,
        bool hasListed,
        bool hasActive,
        CancellationToken ct)
    {
        var sinceUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(recentWindowDays);

        var messageJoin = hasMessages
            ? @"LEFT JOIN messages m
       ON m.chat_id = r.chat_id
      AND m.message_id = r.message_id"
            : string.Empty;

        var latestUserProfileCte = hasMessages
            ? @"
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
)"
            : string.Empty;

        var chatRegistryJoin = hasChatRegistry
            ? @"
LEFT JOIN chat_registry cr
       ON cr.chat_id = r.chat_id"
            : string.Empty;

        var latestUserJoin = hasMessages
            ? @"
LEFT JOIN latest_user_profile lup
       ON lup.chat_id = r.chat_id
      AND lup.user_id = r.user_id"
            : string.Empty;

        var chatTitleExpr = hasMessages && hasChatRegistry
            ? "COALESCE(m.chat_title, cr.chat_title)"
            : hasMessages
                ? "m.chat_title"
                : hasChatRegistry
                    ? "cr.chat_title"
                    : "NULL";
        var currentScopeExpr = hasChatRegistry && hasListed
            ? "COALESCE(cr.is_listed, 0)"
            : "NULL";
        var chatActiveExpr = hasChatRegistry && hasActive
            ? "cr.is_active"
            : "NULL";

        var usernameExpr = hasMessages
            ? "COALESCE(m.user_username, lup.user_username)"
            : "NULL";

        var firstNameExpr = hasMessages
            ? "COALESCE(m.user_first_name, lup.user_first_name)"
            : "NULL";

        var lastNameExpr = hasMessages
            ? "COALESCE(m.user_last_name, lup.user_last_name)"
            : "NULL";

        var textExpr = hasMessages ? "m.text" : "NULL";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
{latestUserProfileCte}
SELECT r.event_at_utc,
       r.chat_id,
       {chatTitleExpr} AS chat_title,
       r.user_id,
       {usernameExpr} AS username,
       {firstNameExpr} AS first_name,
       {lastNameExpr} AS last_name,
       r.message_id,
       r.label,
       r.topic,
       r.intent,
       r.confidence,
       r.risk_score,
       r.needs_context_review,
       {textExpr} AS message_text,
       r.reason,
       {currentScopeExpr} AS in_current_bot_scope,
       {chatActiveExpr} AS chat_is_active
FROM risk_events r
{messageJoin}
{chatRegistryJoin}
{latestUserJoin}
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

        var list = new List<AdminIncidentFeedItemDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AdminIncidentFeedItemDto
            {
                IncidentId = string.Empty,
                EventAtUtc = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
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
                Reason = reader.IsDBNull(15) ? null : reader.GetString(15),
                InCurrentBotScope = ReadNullableBool(reader, 16),
                ChatIsActive = ReadNullableBool(reader, 17),
                IsUnread = false,
                IsAcknowledged = false
            });
        }

        return list;
    }

    private static bool ShouldExposeIncidentEntry(AdminIncidentFeedItemDto entry)
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

    private static string BuildIncidentGroupKey(AdminIncidentFeedItemDto entry)
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

    private static double GetIncidentPriorityScore(AdminIncidentFeedItemDto entry)
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

    private static AdminIncidentFeedResponseDto CreateEmptyResponse(
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        IReadOnlyList<string> warnings)
    {
        return new AdminIncidentFeedResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = 0,
            ReturnedCount = 0,
            ChatIdFilter = chatId,
            RecentWindowDays = recentWindowDays,
            Summary = new AdminIncidentFeedSummaryDto
            {
                CurrentBotScopeApplied = false,
                HistoricalVisibilityEnabled = false,
                LegacySchemaMode = true
            },
            Warnings = warnings
        };
    }

    private static IncidentFeedSnapshot CreateEmptySnapshot(
        long? chatId,
        int recentWindowDays,
        IReadOnlyList<string> warnings)
    {
        return new IncidentFeedSnapshot(
            Items: Array.Empty<AdminIncidentFeedItemDto>(),
            Summary: new AdminIncidentFeedSummaryDto
            {
                CurrentBotScopeApplied = false,
                HistoricalVisibilityEnabled = false,
                LegacySchemaMode = true,
                UnreadCount = 0,
                AcknowledgedCount = 0,
                RequiresAttentionCount = 0
            },
            Warnings: warnings,
            ChatIdFilter: chatId,
            RecentWindowDays: recentWindowDays);
    }

    internal static string BuildIncidentId(AdminIncidentFeedItemDto item)
    {
        var source = string.Join("|",
            item.ChatId.ToString(CultureInfo.InvariantCulture),
            item.UserId.ToString(CultureInfo.InvariantCulture),
            item.MessageId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            item.Label ?? "none",
            item.Topic ?? "none",
            item.Intent ?? "none",
            item.EventAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            item.RiskScore.ToString("0.00", CultureInfo.InvariantCulture));

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes[..12]).ToLowerInvariant();
    }

    private static bool? ReadNullableBool(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetInt64(ordinal) == 1;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
            return false;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public sealed record IncidentFeedSnapshot(
        IReadOnlyList<AdminIncidentFeedItemDto> Items,
        AdminIncidentFeedSummaryDto Summary,
        IReadOnlyList<string> Warnings,
        long? ChatIdFilter,
        int RecentWindowDays);
}
