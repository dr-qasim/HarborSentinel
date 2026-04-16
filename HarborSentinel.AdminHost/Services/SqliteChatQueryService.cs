using HarborSentinel.AdminContracts.Chats;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace HarborSentinel.AdminHost.Services;

public sealed class SqliteChatQueryService : IChatQueryService
{
    private static readonly TimeSpan DetailRecentWindow = TimeSpan.FromDays(7);

    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly ILogger<SqliteChatQueryService> _log;

    public SqliteChatQueryService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        ILogger<SqliteChatQueryService> log)
    {
        _runtimeOptions = runtimeOptions.Value;
        _log = log;
    }

    public async Task<AdminChatListResponseDto> GetChatListAsync(
        int page,
        int pageSize,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var warnings = new List<string>();
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        if (!File.Exists(databasePath))
        {
            warnings.Add("Файл runtime-базы данных не найден. Список чатов недоступен.");
            return CreateEmptyResponse(page, pageSize, activityFilter, trackingFilter, warnings);
        }

        try
        {
            await using var connection = OpenReadOnly(databasePath);

            if (!await TableExistsAsync(connection, "chat_registry", ct).ConfigureAwait(false))
            {
                warnings.Add("Таблица chat_registry отсутствует. Реестр чатов недоступен.");
                return CreateEmptyResponse(page, pageSize, activityFilter, trackingFilter, warnings);
            }

            var schema = await ReadRegistrySchemaAsync(connection, ct).ConfigureAwait(false);
            ApplySchemaWarnings(
                warnings,
                schema,
                activityFilter,
                trackingFilter);

            var whereClause = BuildListWhereClause(schema, activityFilter, trackingFilter);
            var summary = new AdminChatListSummaryDto
            {
                VisibleChats = await CountAsync(connection, schema.HasListed ? "WHERE is_listed = 1" : string.Empty, ct).ConfigureAwait(false),
                ActiveChats = schema.HasActive
                    ? await CountAsync(connection, BuildSummaryWhere(schema.HasListed, "is_active = 1"), ct).ConfigureAwait(false)
                    : null,
                RemovedChats = schema.HasActive
                    ? await CountAsync(connection, BuildSummaryWhere(schema.HasListed, "is_active = 0"), ct).ConfigureAwait(false)
                    : null,
                TrackedChats = schema.HasTracked
                    ? await CountAsync(connection, BuildSummaryWhere(schema.HasListed, "is_tracked = 1"), ct).ConfigureAwait(false)
                    : null,
                CurrentBotScopeApplied = schema.HasListed,
                LegacySchemaMode = !schema.HasListed || !schema.HasTracked
            };

            var totalCount = await CountAsync(connection, whereClause, ct).ConfigureAwait(false);
            var items = await LoadItemsAsync(connection, whereClause, page, pageSize, schema, ct).ConfigureAwait(false);

            return new AdminChatListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                ReturnedCount = items.Count,
                ActivityFilter = activityFilter,
                TrackingFilter = trackingFilter,
                Summary = summary,
                Items = items,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to query chat list from runtime database {Path}", databasePath);
            warnings.Add("Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost.");
            return CreateEmptyResponse(page, pageSize, activityFilter, trackingFilter, warnings);
        }
    }

    public async Task<AdminChatDetailDto?> GetChatDetailAsync(long chatId, CancellationToken ct)
    {
        var warnings = new List<string>();
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        if (!File.Exists(databasePath))
            return null;

        try
        {
            await using var connection = OpenReadOnly(databasePath);
            if (!await TableExistsAsync(connection, "chat_registry", ct).ConfigureAwait(false))
                return null;

            var schema = await ReadRegistrySchemaAsync(connection, ct).ConfigureAwait(false);
            ApplySchemaWarnings(
                warnings,
                schema,
                AdminChatActivityFilterDto.All,
                AdminChatTrackingFilterDto.All);

            var detail = await LoadChatDetailAsync(connection, chatId, schema, warnings, ct).ConfigureAwait(false);
            return detail;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to query chat detail {ChatId} from runtime database {Path}", chatId, databasePath);
            return new AdminChatDetailDto
            {
                ChatId = chatId,
                CurrentBotScopeApplied = false,
                LegacySchemaMode = true,
                RecentWindowDays = (int)DetailRecentWindow.TotalDays,
                Moderation = new AdminChatModerationSummaryDto(),
                Vigilance = new AdminChatVigilanceStateDto(),
                Warnings =
                [
                    "Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost."
                ]
            };
        }
    }

    private static void ApplySchemaWarnings(
        ICollection<string> warnings,
        ChatRegistrySchema schema,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter)
    {
        if (!schema.HasListed)
            warnings.Add("Обнаружена старая схема chat_registry: изоляция по текущему боту недоступна.");

        if (!schema.HasTracked && trackingFilter != AdminChatTrackingFilterDto.All)
            warnings.Add("Фильтр по отслеживанию недоступен для этой схемы runtime и был проигнорирован.");

        if (!schema.HasActive && activityFilter != AdminChatActivityFilterDto.All)
            warnings.Add("Фильтр по активности недоступен для этой схемы runtime и был проигнорирован.");
    }

    private static string BuildListWhereClause(
        ChatRegistrySchema schema,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter)
    {
        var whereParts = new List<string>();
        if (schema.HasListed)
            whereParts.Add("is_listed = 1");

        if (schema.HasActive)
        {
            if (activityFilter == AdminChatActivityFilterDto.Active)
                whereParts.Add("is_active = 1");
            else if (activityFilter == AdminChatActivityFilterDto.Removed)
                whereParts.Add("is_active = 0");
        }

        if (schema.HasTracked)
        {
            if (trackingFilter == AdminChatTrackingFilterDto.Tracked)
                whereParts.Add("is_tracked = 1");
            else if (trackingFilter == AdminChatTrackingFilterDto.Untracked)
                whereParts.Add("is_tracked = 0");
        }

        return whereParts.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", whereParts);
    }

    private static async Task<ChatRegistrySchema> ReadRegistrySchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        return new ChatRegistrySchema
        {
            HasListed = await ColumnExistsAsync(connection, "chat_registry", "is_listed", ct).ConfigureAwait(false),
            HasActive = await ColumnExistsAsync(connection, "chat_registry", "is_active", ct).ConfigureAwait(false),
            HasTracked = await ColumnExistsAsync(connection, "chat_registry", "is_tracked", ct).ConfigureAwait(false),
            HasStatus = await ColumnExistsAsync(connection, "chat_registry", "status", ct).ConfigureAwait(false),
            HasUsername = await ColumnExistsAsync(connection, "chat_registry", "chat_username", ct).ConfigureAwait(false),
            HasTitle = await ColumnExistsAsync(connection, "chat_registry", "chat_title", ct).ConfigureAwait(false),
            HasType = await ColumnExistsAsync(connection, "chat_registry", "chat_type", ct).ConfigureAwait(false),
            HasFirstSeen = await ColumnExistsAsync(connection, "chat_registry", "first_seen_utc", ct).ConfigureAwait(false),
            HasLastSeen = await ColumnExistsAsync(connection, "chat_registry", "last_seen_utc", ct).ConfigureAwait(false),
            HasRemovedUtc = await ColumnExistsAsync(connection, "chat_registry", "removed_utc", ct).ConfigureAwait(false)
        };
    }

    private static async Task<IReadOnlyList<AdminChatListItemDto>> LoadItemsAsync(
        SqliteConnection connection,
        string whereClause,
        int page,
        int pageSize,
        ChatRegistrySchema schema,
        CancellationToken ct)
    {
        var selectParts = new[]
        {
            "chat_id",
            schema.HasTitle ? "chat_title" : "NULL AS chat_title",
            schema.HasUsername ? "chat_username" : "NULL AS chat_username",
            schema.HasType ? "chat_type" : "NULL AS chat_type",
            schema.HasStatus ? "status" : "NULL AS status",
            schema.HasActive ? "is_active" : "NULL AS is_active",
            schema.HasTracked ? "is_tracked" : "NULL AS is_tracked",
            schema.HasFirstSeen ? "first_seen_utc" : "NULL AS first_seen_utc",
            schema.HasLastSeen ? "last_seen_utc" : "NULL AS last_seen_utc",
            schema.HasRemovedUtc ? "removed_utc" : "NULL AS removed_utc"
        };

        var orderBy = schema.HasLastSeen ? "ORDER BY last_seen_utc DESC, chat_id ASC" : "ORDER BY chat_id ASC";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {string.Join(", ", selectParts)}
FROM chat_registry
{whereClause}
{orderBy}
LIMIT $lim OFFSET $off;";
        cmd.Parameters.AddWithValue("$lim", pageSize);
        cmd.Parameters.AddWithValue("$off", (page - 1) * pageSize);

        var items = new List<AdminChatListItemDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new AdminChatListItemDto
            {
                ChatId = reader.GetInt64(0),
                ChatTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
                ChatUsername = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChatType = reader.IsDBNull(3) ? null : reader.GetString(3),
                Status = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive = ReadNullableBool(reader, 5),
                IsTracked = ReadNullableBool(reader, 6),
                FirstSeenUtc = ReadNullableTimestamp(reader, 7),
                LastSeenUtc = ReadNullableTimestamp(reader, 8),
                RemovedUtc = ReadNullableTimestamp(reader, 9)
            });
        }

        return items;
    }

    private static async Task<AdminChatDetailDto?> LoadChatDetailAsync(
        SqliteConnection connection,
        long chatId,
        ChatRegistrySchema schema,
        List<string> warnings,
        CancellationToken ct)
    {
        var selectParts = new[]
        {
            "chat_id",
            schema.HasTitle ? "chat_title" : "NULL AS chat_title",
            schema.HasUsername ? "chat_username" : "NULL AS chat_username",
            schema.HasType ? "chat_type" : "NULL AS chat_type",
            schema.HasStatus ? "status" : "NULL AS status",
            schema.HasActive ? "is_active" : "NULL AS is_active",
            schema.HasTracked ? "is_tracked" : "NULL AS is_tracked",
            schema.HasFirstSeen ? "first_seen_utc" : "NULL AS first_seen_utc",
            schema.HasLastSeen ? "last_seen_utc" : "NULL AS last_seen_utc",
            schema.HasRemovedUtc ? "removed_utc" : "NULL AS removed_utc"
        };

        var scopeClause = schema.HasListed ? "AND is_listed = 1" : string.Empty;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {string.Join(", ", selectParts)}
FROM chat_registry
WHERE chat_id = $chatId
  {scopeClause}
LIMIT 1;";
        cmd.Parameters.AddWithValue("$chatId", chatId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var moderation = await LoadModerationSummaryAsync(connection, chatId, warnings, ct).ConfigureAwait(false);
        var vigilance = await LoadVigilanceStateAsync(connection, chatId, warnings, ct).ConfigureAwait(false);

        return new AdminChatDetailDto
        {
            ChatId = reader.GetInt64(0),
            ChatTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
            ChatUsername = reader.IsDBNull(2) ? null : reader.GetString(2),
            ChatType = reader.IsDBNull(3) ? null : reader.GetString(3),
            Status = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsActive = ReadNullableBool(reader, 5),
            IsTracked = ReadNullableBool(reader, 6),
            FirstSeenUtc = ReadNullableTimestamp(reader, 7),
            LastSeenUtc = ReadNullableTimestamp(reader, 8),
            RemovedUtc = ReadNullableTimestamp(reader, 9),
            CurrentBotScopeApplied = schema.HasListed,
            LegacySchemaMode = !schema.HasListed || !schema.HasTracked,
            RecentWindowDays = (int)DetailRecentWindow.TotalDays,
            Moderation = moderation,
            Vigilance = vigilance,
            Warnings = warnings
        };
    }

    private static async Task<AdminChatModerationSummaryDto> LoadModerationSummaryAsync(
        SqliteConnection connection,
        long chatId,
        List<string> warnings,
        CancellationToken ct)
    {
        var summary = new AdminChatModerationSummaryDto();
        var sinceUtc = DateTimeOffset.UtcNow - DetailRecentWindow;

        var messagesExists = await TableExistsAsync(connection, "messages", ct).ConfigureAwait(false);
        var membersExists = await TableExistsAsync(connection, "chat_members", ct).ConfigureAwait(false);
        var flaggedExists = await TableExistsAsync(connection, "flagged_events", ct).ConfigureAwait(false);
        var riskExists = await TableExistsAsync(connection, "risk_events", ct).ConfigureAwait(false);
        var membersHasAdmin = membersExists && await ColumnExistsAsync(connection, "chat_members", "is_admin", ct).ConfigureAwait(false);

        if (!messagesExists)
            warnings.Add("Таблица messages отсутствует. Сводка по сообщениям недоступна.");

        if (!membersExists)
            warnings.Add("Таблица chat_members отсутствует. Сводка по участникам недоступна.");

        if (!flaggedExists)
            warnings.Add("Таблица flagged_events отсутствует. Сводка по flagged-событиям недоступна.");

        if (!riskExists)
            warnings.Add("Таблица risk_events отсутствует. Сводка по risk-событиям недоступна.");

        long? messageCount = null;
        DateTimeOffset? lastMessageUtc = null;
        int? participantCount = null;
        int? adminParticipantCount = null;
        int? flaggedUserCount = null;
        long? flaggedEventCount = null;
        long? recentFlaggedEventCount = null;
        DateTimeOffset? lastFlaggedUtc = null;
        long? riskEventCount = null;
        long? recentRiskEventCount = null;
        DateTimeOffset? lastRiskUtc = null;
        string? lastRiskLabel = null;
        string? lastRiskTopic = null;
        string? lastRiskIntent = null;
        string? lastRiskReason = null;
        double? lastRiskScore = null;

        if (messagesExists)
        {
            messageCount = await ExecuteLongScalarAsync(
                connection,
                "SELECT COUNT(1) FROM messages WHERE chat_id = $chat;",
                chatId,
                ct).ConfigureAwait(false);

            lastMessageUtc = await ExecuteTimestampScalarAsync(
                connection,
                "SELECT MAX(timestamp_utc) FROM messages WHERE chat_id = $chat;",
                chatId,
                ct).ConfigureAwait(false);
        }

        if (messagesExists || membersExists)
        {
            participantCount = await CountParticipantsAsync(connection, chatId, messagesExists, membersExists, ct).ConfigureAwait(false);
        }

        if (membersHasAdmin)
        {
            adminParticipantCount = (int?)await ExecuteLongScalarAsync(
                connection,
                "SELECT COUNT(1) FROM chat_members WHERE chat_id = $chat AND is_admin = 1;",
                chatId,
                ct).ConfigureAwait(false);
        }

        if (flaggedExists)
        {
            flaggedUserCount = (int?)await ExecuteLongScalarAsync(
                connection,
                "SELECT COUNT(1) FROM (SELECT DISTINCT user_id FROM flagged_events WHERE chat_id = $chat AND user_id IS NOT NULL);",
                chatId,
                ct).ConfigureAwait(false);

            flaggedEventCount = await ExecuteLongScalarAsync(
                connection,
                "SELECT COUNT(1) FROM flagged_events WHERE chat_id = $chat;",
                chatId,
                ct).ConfigureAwait(false);

            recentFlaggedEventCount = await ExecuteLongScalarAsync(
                connection,
                @"SELECT COUNT(1)
FROM flagged_events
WHERE chat_id = $chat
  AND flagged_at_utc >= $since;",
                chatId,
                ct,
                sinceUtc).ConfigureAwait(false);

            lastFlaggedUtc = await ExecuteTimestampScalarAsync(
                connection,
                "SELECT MAX(flagged_at_utc) FROM flagged_events WHERE chat_id = $chat;",
                chatId,
                ct).ConfigureAwait(false);
        }

        if (riskExists)
        {
            riskEventCount = await ExecuteLongScalarAsync(
                connection,
                "SELECT COUNT(1) FROM risk_events WHERE chat_id = $chat;",
                chatId,
                ct).ConfigureAwait(false);

            recentRiskEventCount = await ExecuteLongScalarAsync(
                connection,
                @"SELECT COUNT(1)
FROM risk_events
WHERE chat_id = $chat
  AND event_at_utc >= $since;",
                chatId,
                ct,
                sinceUtc).ConfigureAwait(false);

            await using var riskCmd = connection.CreateCommand();
            riskCmd.CommandText = @"
SELECT
    MAX(event_at_utc),
    (
        SELECT label
        FROM risk_events
        WHERE chat_id = $chat
        ORDER BY event_at_utc DESC, id DESC
        LIMIT 1
    ),
    (
        SELECT topic
        FROM risk_events
        WHERE chat_id = $chat
        ORDER BY event_at_utc DESC, id DESC
        LIMIT 1
    ),
    (
        SELECT intent
        FROM risk_events
        WHERE chat_id = $chat
        ORDER BY event_at_utc DESC, id DESC
        LIMIT 1
    ),
    (
        SELECT reason
        FROM risk_events
        WHERE chat_id = $chat
        ORDER BY event_at_utc DESC, id DESC
        LIMIT 1
    ),
    (
        SELECT risk_score
        FROM risk_events
        WHERE chat_id = $chat
        ORDER BY event_at_utc DESC, id DESC
        LIMIT 1
    )
FROM risk_events
WHERE chat_id = $chat;";
            riskCmd.Parameters.AddWithValue("$chat", chatId);
            await using var riskReader = await riskCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await riskReader.ReadAsync(ct).ConfigureAwait(false))
            {
                lastRiskUtc = ReadNullableTimestamp(riskReader, 0);
                lastRiskLabel = riskReader.IsDBNull(1) ? null : riskReader.GetString(1);
                lastRiskTopic = riskReader.IsDBNull(2) ? null : riskReader.GetString(2);
                lastRiskIntent = riskReader.IsDBNull(3) ? null : riskReader.GetString(3);
                lastRiskReason = riskReader.IsDBNull(4) ? null : riskReader.GetString(4);
                lastRiskScore = riskReader.IsDBNull(5) ? null : riskReader.GetDouble(5);
            }
        }

        return new AdminChatModerationSummaryDto
        {
            MessageCount = messageCount,
            LastMessageUtc = lastMessageUtc,
            ParticipantCount = participantCount,
            AdminParticipantCount = adminParticipantCount,
            FlaggedUserCount = flaggedUserCount,
            FlaggedEventCount = flaggedEventCount,
            RecentFlaggedEventCount = recentFlaggedEventCount,
            LastFlaggedUtc = lastFlaggedUtc,
            RiskEventCount = riskEventCount,
            RecentRiskEventCount = recentRiskEventCount,
            LastRiskUtc = lastRiskUtc,
            LastRiskLabel = lastRiskLabel,
            LastRiskTopic = lastRiskTopic,
            LastRiskIntent = lastRiskIntent,
            LastRiskReason = lastRiskReason,
            LastRiskScore = lastRiskScore
        };
    }

    private static async Task<int?> CountParticipantsAsync(
        SqliteConnection connection,
        long chatId,
        bool messagesExists,
        bool membersExists,
        CancellationToken ct)
    {
        string query;
        if (messagesExists && membersExists)
        {
            query = @"
SELECT COUNT(1)
FROM (
    SELECT user_id FROM chat_members WHERE chat_id = $chat
    UNION
    SELECT user_id FROM messages WHERE chat_id = $chat AND user_id IS NOT NULL
);";
        }
        else if (membersExists)
        {
            query = @"
SELECT COUNT(1)
FROM (
    SELECT user_id FROM chat_members WHERE chat_id = $chat
);";
        }
        else if (messagesExists)
        {
            query = @"
SELECT COUNT(1)
FROM (
    SELECT DISTINCT user_id FROM messages WHERE chat_id = $chat AND user_id IS NOT NULL
);";
        }
        else
        {
            return null;
        }

        var count = await ExecuteLongScalarAsync(connection, query, chatId, ct).ConfigureAwait(false);
        return checked((int?)count);
    }

    private static async Task<AdminChatVigilanceStateDto> LoadVigilanceStateAsync(
        SqliteConnection connection,
        long chatId,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "chat_vigilance_state", ct).ConfigureAwait(false))
        {
            warnings.Add("Таблица chat_vigilance_state отсутствует. Состояние повышенной бдительности недоступно.");
            return new AdminChatVigilanceStateDto();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
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
            return new AdminChatVigilanceStateDto();

        var heightenedUntil = ReadNullableTimestamp(reader, 0);
        return new AdminChatVigilanceStateDto
        {
            HasStateRecord = true,
            IsHeightenedNow = heightenedUntil.HasValue && heightenedUntil.Value >= DateTimeOffset.UtcNow,
            HeightenedUntilUtc = heightenedUntil,
            LastTriggeredAtUtc = ReadNullableTimestamp(reader, 1),
            TriggerCount = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            LastLabel = reader.IsDBNull(3) ? null : reader.GetString(3),
            LastTopic = reader.IsDBNull(4) ? null : reader.GetString(4),
            LastRiskLevel = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastReason = reader.IsDBNull(6) ? null : reader.GetString(6),
            LastScore = reader.IsDBNull(7) ? null : reader.GetDouble(7)
        };
    }

    private static async Task<long?> ExecuteLongScalarAsync(
        SqliteConnection connection,
        string commandText,
        long chatId,
        CancellationToken ct,
        DateTimeOffset? sinceUtc = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = commandText;
        cmd.Parameters.AddWithValue("$chat", chatId);
        if (sinceUtc.HasValue)
            cmd.Parameters.AddWithValue("$since", sinceUtc.Value.UtcDateTime.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null || result is DBNull)
            return null;

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> ExecuteTimestampScalarAsync(
        SqliteConnection connection,
        string commandText,
        long chatId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = commandText;
        cmd.Parameters.AddWithValue("$chat", chatId);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return ParseTimestamp(result as string);
    }

    private static string BuildSummaryWhere(bool hasListed, string additionalClause)
    {
        if (hasListed)
            return $"WHERE is_listed = 1 AND {additionalClause}";

        return $"WHERE {additionalClause}";
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string whereClause, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(1) FROM chat_registry {whereClause};";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0, CultureInfo.InvariantCulture);
    }

    private static AdminChatListResponseDto CreateEmptyResponse(
        int page,
        int pageSize,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter,
        IReadOnlyList<string> warnings)
    {
        return new AdminChatListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = 0,
            ReturnedCount = 0,
            ActivityFilter = activityFilter,
            TrackingFilter = trackingFilter,
            Summary = new AdminChatListSummaryDto
            {
                VisibleChats = 0,
                CurrentBotScopeApplied = false,
                LegacySchemaMode = true
            },
            Warnings = warnings
        };
    }

    private static bool? ReadNullableBool(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetInt64(ordinal) == 1;
    }

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return ParseTimestamp(reader.GetString(ordinal));
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
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

    private sealed class ChatRegistrySchema
    {
        public bool HasListed { get; init; }
        public bool HasActive { get; init; }
        public bool HasTracked { get; init; }
        public bool HasStatus { get; init; }
        public bool HasUsername { get; init; }
        public bool HasTitle { get; init; }
        public bool HasType { get; init; }
        public bool HasFirstSeen { get; init; }
        public bool HasLastSeen { get; init; }
        public bool HasRemovedUtc { get; init; }
    }
}
