using HarborSentinel.AdminContracts.Users;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace HarborSentinel.AdminHost.Services;

public sealed class SqliteUserQueryService : IUserQueryService
{
    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly ILogger<SqliteUserQueryService> _log;

    public SqliteUserQueryService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        ILogger<SqliteUserQueryService> log)
    {
        _runtimeOptions = runtimeOptions.Value;
        _log = log;
    }

    public async Task<AdminUserListResponseDto> GetUsersAsync(
        int page,
        int pageSize,
        AdminUserListSourceFilterDto sourceFilter,
        string? searchQuery,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var warnings = new List<string>();
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        if (!File.Exists(databasePath))
        {
            warnings.Add("Файл runtime-базы данных не найден. Реестр пользователей недоступен.");
            return CreateEmptyResponse(page, pageSize, sourceFilter, warnings);
        }

        try
        {
            await using var connection = OpenReadOnly(databasePath);
            var schema = await ReadSchemaAsync(connection, warnings, ct).ConfigureAwait(false);
            var users = await LoadUsersAsync(connection, schema, ct).ConfigureAwait(false);

            var normalizedSearchQuery = NormalizeSearchQuery(searchQuery);

            var filtered = users
                .Where(item => sourceFilter switch
                {
                    AdminUserListSourceFilterDto.PrivateStart => item.HasPrivateStart,
                    AdminUserListSourceFilterDto.Flagged => (item.FlaggedEventCount ?? 0) > 0,
                    _ => true
                })
                .Where(item => MatchesSearch(item, normalizedSearchQuery))
                .OrderByDescending(static item => item.InCurrentBotScope is true)
                .ThenByDescending(static item => item.LastActivityUtc)
                .ThenByDescending(static item => item.FlaggedEventCount ?? 0)
                .ThenByDescending(static item => item.MessageCount ?? 0)
                .ThenByDescending(static item => item.UserId)
                .ToArray();

            var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            var summary = new AdminUserListSummaryDto
            {
                VisibleUsers = filtered.Length,
                PrivateStartUsers = filtered.Count(static item => item.HasPrivateStart),
                FlaggedUsers = filtered.Count(static item => (item.FlaggedEventCount ?? 0) > 0),
                UsersWithPhone = filtered.Count(static item => item.HasPhone),
                CurrentScopeUsers = schema.ApplyScope ? filtered.Count(static item => item.InCurrentBotScope is true) : 0,
                HistoricalOnlyUsers = schema.ApplyScope ? filtered.Count(static item => item.InCurrentBotScope is false) : 0,
                CurrentBotScopeApplied = schema.ApplyScope,
                HistoricalVisibilityEnabled = schema.ApplyScope,
                LegacySchemaMode = !schema.ApplyScope
            };

            return new AdminUserListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = filtered.Length,
                ReturnedCount = paged.Length,
                SourceFilter = sourceFilter,
                SearchQuery = normalizedSearchQuery,
                Summary = summary,
                Items = paged,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to query users from runtime database {Path}", databasePath);
            warnings.Add("Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost.");
            return CreateEmptyResponse(page, pageSize, sourceFilter, warnings);
        }
    }

    public async Task<AdminUserDetailDto?> GetUserDetailAsync(long userId, CancellationToken ct)
    {
        var warnings = new List<string>();
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        if (!File.Exists(databasePath))
            return null;

        try
        {
            await using var connection = OpenReadOnly(databasePath);
            var schema = await ReadSchemaAsync(connection, warnings, ct).ConfigureAwait(false);
            var detail = await LoadUserDetailAsync(connection, schema, userId, warnings, ct).ConfigureAwait(false);
            return detail;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to query user detail {UserId} from runtime database {Path}", userId, databasePath);
            return new AdminUserDetailDto
            {
                UserId = userId,
                CurrentBotScopeApplied = false,
                LegacySchemaMode = true,
                Warnings =
                [
                    "Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost."
                ]
            };
        }
    }

    private static async Task<UserQuerySchema> ReadSchemaAsync(
        SqliteConnection connection,
        List<string> warnings,
        CancellationToken ct)
    {
        var messagesExists = await TableExistsAsync(connection, "messages", ct).ConfigureAwait(false);
        var membersExists = await TableExistsAsync(connection, "chat_members", ct).ConfigureAwait(false);
        var privateStartExists = await TableExistsAsync(connection, "private_start_users", ct).ConfigureAwait(false);
        var flaggedExists = await TableExistsAsync(connection, "flagged_events", ct).ConfigureAwait(false);
        var riskExists = await TableExistsAsync(connection, "risk_events", ct).ConfigureAwait(false);
        var chatRegistryExists = await TableExistsAsync(connection, "chat_registry", ct).ConfigureAwait(false);
        var chatRegistryHasListed = chatRegistryExists && await ColumnExistsAsync(connection, "chat_registry", "is_listed", ct).ConfigureAwait(false);
        var chatRegistryHasTracked = chatRegistryExists && await ColumnExistsAsync(connection, "chat_registry", "is_tracked", ct).ConfigureAwait(false);
        var chatRegistryHasActive = chatRegistryExists && await ColumnExistsAsync(connection, "chat_registry", "is_active", ct).ConfigureAwait(false);

        if (!messagesExists)
            warnings.Add("Таблица messages отсутствует. Сводка по сообщениям пользователей недоступна.");
        if (!membersExists)
            warnings.Add("Таблица chat_members отсутствует. Сводка по участникам будет неполной.");
        if (!privateStartExists)
            warnings.Add("Таблица private_start_users отсутствует. private-start контур недоступен.");
        if (!flaggedExists)
            warnings.Add("Таблица flagged_events отсутствует. Сводка по flagged-пользователям недоступна.");
        if (!riskExists)
            warnings.Add("Таблица risk_events отсутствует. Сводка по risk-событиям недоступна.");
        if (!chatRegistryExists)
            warnings.Add("Таблица chat_registry отсутствует. Изоляция по текущему боту недоступна.");
        else if (!chatRegistryHasListed)
            warnings.Add("Обнаружена старая схема chat_registry: изоляция пользователей по текущему боту недоступна.");

        return new UserQuerySchema
        {
            MessagesExists = messagesExists,
            MembersExists = membersExists,
            PrivateStartExists = privateStartExists,
            FlaggedExists = flaggedExists,
            RiskExists = riskExists,
            ChatRegistryExists = chatRegistryExists,
            ChatRegistryHasListed = chatRegistryHasListed,
            ChatRegistryHasTracked = chatRegistryHasTracked,
            ChatRegistryHasActive = chatRegistryHasActive,
            ApplyScope = chatRegistryExists && chatRegistryHasListed
        };
    }

    private static async Task<IReadOnlyList<AdminUserListItemDto>> LoadUsersAsync(
        SqliteConnection connection,
        UserQuerySchema schema,
        CancellationToken ct)
    {
        var ctes = BuildUserListCtes(schema);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
{ctes}
SELECT u.user_id,
       COALESCE(ps.username, mp.user_username, mup.username) AS username,
       COALESCE(ps.first_name, mp.user_first_name, mup.first_name) AS first_name,
       COALESCE(ps.last_name, mp.user_last_name, mup.last_name) AS last_name,
       CASE WHEN ps.user_id IS NOT NULL THEN 1 ELSE 0 END AS has_private_start,
       ps.start_count,
       CASE WHEN COALESCE(ps.phone, '') <> '' THEN 1 ELSE 0 END AS has_phone,
       cc.chat_count,
       mc.message_count,
       fc.flagged_count,
       rc.risk_count,
       MAX(COALESCE(mc.last_message_utc, ''), COALESCE(mup.last_member_seen_utc, ''), COALESCE(ps.last_started_at_utc, '')) AS last_activity_utc,
       {(schema.ApplyScope ? "CASE WHEN csu.user_id IS NOT NULL OR ps.user_id IS NOT NULL THEN 1 ELSE 0 END" : "NULL")} AS in_current_scope
FROM all_users u
LEFT JOIN private_start_scope ps ON ps.user_id = u.user_id
LEFT JOIN latest_message_profile mp ON mp.user_id = u.user_id
LEFT JOIN latest_member_profile mup ON mup.user_id = u.user_id
LEFT JOIN message_counts mc ON mc.user_id = u.user_id
LEFT JOIN chat_counts cc ON cc.user_id = u.user_id
LEFT JOIN flagged_counts fc ON fc.user_id = u.user_id
LEFT JOIN risk_counts rc ON rc.user_id = u.user_id
LEFT JOIN current_scope_users csu ON csu.user_id = u.user_id
ORDER BY last_activity_utc DESC, u.user_id DESC;";

        var list = new List<AdminUserListItemDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AdminUserListItemDto
            {
                UserId = reader.GetInt64(0),
                Username = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstName = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastName = reader.IsDBNull(3) ? null : reader.GetString(3),
                HasPrivateStart = !reader.IsDBNull(4) && reader.GetInt64(4) == 1,
                PrivateStartCount = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                HasPhone = !reader.IsDBNull(6) && reader.GetInt64(6) == 1,
                ChatCount = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                MessageCount = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                FlaggedEventCount = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                RiskEventCount = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                LastActivityUtc = reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
                InCurrentBotScope = ReadNullableBool(reader, 12)
            });
        }

        return list;
    }

    private static async Task<AdminUserDetailDto?> LoadUserDetailAsync(
        SqliteConnection connection,
        UserQuerySchema schema,
        long userId,
        List<string> warnings,
        CancellationToken ct)
    {
        var users = await LoadUsersAsync(connection, schema, ct).ConfigureAwait(false);
        var item = users.FirstOrDefault(x => x.UserId == userId);
        if (item is null)
            return null;

        string? rawPhone = null;
        DateTimeOffset? startedAtUtc = null;
        DateTimeOffset? lastStartedAtUtc = null;
        int? startCount = item.PrivateStartCount;

        if (schema.PrivateStartExists)
        {
            await using var psCmd = connection.CreateCommand();
            psCmd.CommandText = @"
SELECT phone, started_at_utc, last_started_at_utc, start_count
FROM private_start_users
WHERE user_id = $uid
LIMIT 1;";
            psCmd.Parameters.AddWithValue("$uid", userId);
            await using var reader = await psCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rawPhone = reader.IsDBNull(0) ? null : reader.GetString(0);
                startedAtUtc = reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1));
                lastStartedAtUtc = reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2));
                startCount = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }

        var chats = await LoadUserChatsAsync(connection, schema, userId, ct).ConfigureAwait(false);

        return new AdminUserDetailDto
        {
            UserId = item.UserId,
            Username = item.Username,
            FirstName = item.FirstName,
            LastName = item.LastName,
            InCurrentBotScope = item.InCurrentBotScope,
            HasPrivateStart = item.HasPrivateStart,
            MaskedPhone = MaskPhone(rawPhone),
            StartedAtUtc = startedAtUtc,
            LastStartedAtUtc = lastStartedAtUtc,
            StartCount = startCount,
            ChatCount = item.ChatCount,
            MessageCount = item.MessageCount,
            FlaggedEventCount = item.FlaggedEventCount,
            RiskEventCount = item.RiskEventCount,
            LastActivityUtc = item.LastActivityUtc,
            CurrentBotScopeApplied = schema.ApplyScope,
            LegacySchemaMode = !schema.ApplyScope,
            Chats = chats,
            Warnings = warnings
        };
    }

    private static async Task<IReadOnlyList<AdminUserChatSummaryDto>> LoadUserChatsAsync(
        SqliteConnection connection,
        UserQuerySchema schema,
        long userId,
        CancellationToken ct)
    {
        var ctes = BuildUserDetailChatCtes(schema);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
{ctes}
SELECT c.chat_id,
       COALESCE(cr.chat_title, m.chat_title) AS chat_title,
       COALESCE(cr.chat_username, m.chat_username) AS chat_username,
       {(schema.ApplyScope ? "COALESCE(cr.is_listed, 0)" : "NULL")} AS in_current_scope,
       {(schema.ChatRegistryHasTracked ? "cr.is_tracked" : "NULL")} AS is_tracked,
       {(schema.ChatRegistryHasActive ? "cr.is_active" : "NULL")} AS is_active,
       mc.message_count,
       fc.flagged_count,
       rc.risk_count,
       MAX(COALESCE(mc.last_message_utc, ''), COALESCE(c.last_member_seen_utc, '')) AS last_seen_utc
FROM candidate_chats c
LEFT JOIN chat_registry_scope cr ON cr.chat_id = c.chat_id
LEFT JOIN latest_chat_title m ON m.chat_id = c.chat_id
LEFT JOIN message_counts mc ON mc.chat_id = c.chat_id
LEFT JOIN flagged_counts fc ON fc.chat_id = c.chat_id
LEFT JOIN risk_counts rc ON rc.chat_id = c.chat_id
ORDER BY last_seen_utc DESC, c.chat_id DESC;";
        cmd.Parameters.AddWithValue("$uid", userId);

        var list = new List<AdminUserChatSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AdminUserChatSummaryDto
            {
                ChatId = reader.GetInt64(0),
                ChatTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
                ChatUsername = reader.IsDBNull(2) ? null : reader.GetString(2),
                InCurrentBotScope = ReadNullableBool(reader, 3),
                IsTracked = ReadNullableBool(reader, 4),
                IsActive = ReadNullableBool(reader, 5),
                MessageCount = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                FlaggedEventCount = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                RiskEventCount = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                LastSeenUtc = reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9))
            });
        }

        return list;
    }

    private static string BuildUserListCtes(UserQuerySchema schema)
    {
        var ctes = new List<string>();

        ctes.Add(schema.MessagesExists
            ? $@"message_scope AS (
    SELECT m.user_id,
           m.chat_id,
           m.timestamp_utc,
           m.user_username,
           m.user_first_name,
           m.user_last_name
    FROM messages m
    WHERE m.user_id IS NOT NULL
)"
            : @"message_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS user_id,
        CAST(NULL AS INTEGER) AS chat_id,
        CAST(NULL AS TEXT) AS timestamp_utc,
        CAST(NULL AS TEXT) AS user_username,
        CAST(NULL AS TEXT) AS user_first_name,
        CAST(NULL AS TEXT) AS user_last_name
    WHERE 1 = 0
)");

        ctes.Add(schema.MembersExists
            ? $@"member_scope AS (
    SELECT cm.user_id,
           cm.chat_id,
           cm.last_seen_utc,
           cm.username,
           cm.first_name,
           cm.last_name
    FROM chat_members cm
    WHERE cm.user_id IS NOT NULL
)"
            : @"member_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS user_id,
        CAST(NULL AS INTEGER) AS chat_id,
        CAST(NULL AS TEXT) AS last_seen_utc,
        CAST(NULL AS TEXT) AS username,
        CAST(NULL AS TEXT) AS first_name,
        CAST(NULL AS TEXT) AS last_name
    WHERE 1 = 0
)");

        ctes.Add(schema.PrivateStartExists
            ? @"private_start_scope AS (
    SELECT user_id,
           first_name,
           last_name,
           username,
           phone,
           started_at_utc,
           last_started_at_utc,
           start_count
    FROM private_start_users
)"
            : @"private_start_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS user_id,
        CAST(NULL AS TEXT) AS first_name,
        CAST(NULL AS TEXT) AS last_name,
        CAST(NULL AS TEXT) AS username,
        CAST(NULL AS TEXT) AS phone,
        CAST(NULL AS TEXT) AS started_at_utc,
        CAST(NULL AS TEXT) AS last_started_at_utc,
        CAST(NULL AS INTEGER) AS start_count
    WHERE 1 = 0
)");

        ctes.Add(schema.FlaggedExists
            ? $@"flagged_scope AS (
    SELECT user_id, chat_id
    FROM flagged_events
    WHERE user_id IS NOT NULL
)"
            : @"flagged_scope AS (
    SELECT CAST(NULL AS INTEGER) AS user_id, CAST(NULL AS INTEGER) AS chat_id
    WHERE 1 = 0
)");

        ctes.Add(schema.RiskExists
            ? $@"risk_scope AS (
    SELECT user_id, chat_id
    FROM risk_events
    WHERE user_id IS NOT NULL
)"
            : @"risk_scope AS (
    SELECT CAST(NULL AS INTEGER) AS user_id, CAST(NULL AS INTEGER) AS chat_id
    WHERE 1 = 0
)");

        ctes.Add(schema.ChatRegistryExists
            ? $@"chat_registry_scope AS (
    SELECT chat_id,
           {(schema.ChatRegistryHasListed ? "is_listed" : "CAST(NULL AS INTEGER) AS is_listed")}
    FROM chat_registry
)"
            : @"chat_registry_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS chat_id,
        CAST(NULL AS INTEGER) AS is_listed
    WHERE 1 = 0
)");

        ctes.Add(@"latest_message_profile AS (
    SELECT user_id, user_username, user_first_name, user_last_name
    FROM (
        SELECT user_id,
               user_username,
               user_first_name,
               user_last_name,
               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY timestamp_utc DESC, chat_id DESC) AS rn
        FROM message_scope
    ) t
    WHERE rn = 1
)");

        ctes.Add(@"latest_member_profile AS (
    SELECT user_id, username, first_name, last_name, last_member_seen_utc
    FROM (
        SELECT user_id,
               username,
               first_name,
               last_name,
               last_seen_utc AS last_member_seen_utc,
               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY last_seen_utc DESC, chat_id DESC) AS rn
        FROM member_scope
    ) t
    WHERE rn = 1
)");

        ctes.Add(@"message_counts AS (
    SELECT user_id,
           COUNT(1) AS message_count,
           MAX(timestamp_utc) AS last_message_utc
    FROM message_scope
    GROUP BY user_id
)");

        ctes.Add(@"chat_counts AS (
    SELECT user_id, COUNT(DISTINCT chat_id) AS chat_count
    FROM (
        SELECT user_id, chat_id FROM message_scope
        UNION
        SELECT user_id, chat_id FROM member_scope
    ) t
    GROUP BY user_id
)");

        ctes.Add(@"flagged_counts AS (
    SELECT user_id, COUNT(1) AS flagged_count
    FROM flagged_scope
    GROUP BY user_id
)");

        ctes.Add(@"risk_counts AS (
    SELECT user_id, COUNT(1) AS risk_count
    FROM risk_scope
    GROUP BY user_id
)");

        ctes.Add(schema.ApplyScope
            ? @"current_scope_users AS (
    SELECT DISTINCT user_id
    FROM (
        SELECT ms.user_id
        FROM message_scope ms
        JOIN chat_registry_scope crs ON crs.chat_id = ms.chat_id
        WHERE crs.is_listed = 1
        UNION
        SELECT ms.user_id
        FROM member_scope ms
        JOIN chat_registry_scope crs ON crs.chat_id = ms.chat_id
        WHERE crs.is_listed = 1
        UNION
        SELECT fs.user_id
        FROM flagged_scope fs
        JOIN chat_registry_scope crs ON crs.chat_id = fs.chat_id
        WHERE crs.is_listed = 1
        UNION
        SELECT rs.user_id
        FROM risk_scope rs
        JOIN chat_registry_scope crs ON crs.chat_id = rs.chat_id
        WHERE crs.is_listed = 1
    ) t
)"
            : @"current_scope_users AS (
    SELECT CAST(NULL AS INTEGER) AS user_id
    WHERE 1 = 0
)");

        ctes.Add(@"observed_users AS (
    SELECT user_id FROM message_scope
    UNION
    SELECT user_id FROM member_scope
    UNION
    SELECT user_id FROM flagged_scope
    UNION
    SELECT user_id FROM risk_scope
)");

        ctes.Add(@"all_users AS (
    SELECT user_id FROM observed_users
    UNION
    SELECT user_id FROM private_start_scope
)");

        return "WITH " + string.Join(",\n", ctes);
    }

    private static string BuildUserDetailChatCtes(UserQuerySchema schema)
    {
        var ctes = new List<string>();

        ctes.Add(schema.ChatRegistryExists
            ? $@"chat_registry_scope AS (
    SELECT chat_id,
           chat_title,
           chat_username,
           {(schema.ChatRegistryHasListed ? "is_listed" : "CAST(NULL AS INTEGER) AS is_listed")},
           {(schema.ChatRegistryHasTracked ? "is_tracked" : "CAST(NULL AS INTEGER) AS is_tracked")},
           {(schema.ChatRegistryHasActive ? "is_active" : "CAST(NULL AS INTEGER) AS is_active")}
    FROM chat_registry
)"
            : @"chat_registry_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS chat_id,
        CAST(NULL AS TEXT) AS chat_title,
        CAST(NULL AS TEXT) AS chat_username,
        CAST(NULL AS INTEGER) AS is_listed,
        CAST(NULL AS INTEGER) AS is_tracked,
        CAST(NULL AS INTEGER) AS is_active
    WHERE 1 = 0
)");

        ctes.Add(schema.MessagesExists
            ? $@"message_scope AS (
    SELECT chat_id, timestamp_utc, chat_title, chat_username
    FROM messages
    WHERE user_id = $uid
)"
            : @"message_scope AS (
    SELECT
        CAST(NULL AS INTEGER) AS chat_id,
        CAST(NULL AS TEXT) AS timestamp_utc,
        CAST(NULL AS TEXT) AS chat_title,
        CAST(NULL AS TEXT) AS chat_username
    WHERE 1 = 0
)");

        ctes.Add(schema.MembersExists
            ? $@"member_scope AS (
    SELECT chat_id, last_seen_utc
    FROM chat_members
    WHERE user_id = $uid
)"
            : @"member_scope AS (
    SELECT CAST(NULL AS INTEGER) AS chat_id, CAST(NULL AS TEXT) AS last_seen_utc
    WHERE 1 = 0
)");

        ctes.Add(schema.FlaggedExists
            ? $@"flagged_scope AS (
    SELECT chat_id
    FROM flagged_events
    WHERE user_id = $uid
)"
            : @"flagged_scope AS (
    SELECT CAST(NULL AS INTEGER) AS chat_id
    WHERE 1 = 0
)");

        ctes.Add(schema.RiskExists
            ? $@"risk_scope AS (
    SELECT chat_id
    FROM risk_events
    WHERE user_id = $uid
)"
            : @"risk_scope AS (
    SELECT CAST(NULL AS INTEGER) AS chat_id
    WHERE 1 = 0
)");

        ctes.Add(@"candidate_chats AS (
    SELECT chat_id, MAX(last_member_seen_utc) AS last_member_seen_utc
    FROM (
        SELECT chat_id, CAST(NULL AS TEXT) AS last_member_seen_utc FROM message_scope
        UNION ALL
        SELECT chat_id, last_seen_utc AS last_member_seen_utc FROM member_scope
        UNION ALL
        SELECT chat_id, CAST(NULL AS TEXT) AS last_member_seen_utc FROM flagged_scope
        UNION ALL
        SELECT chat_id, CAST(NULL AS TEXT) AS last_member_seen_utc FROM risk_scope
    ) t
    WHERE chat_id IS NOT NULL
    GROUP BY chat_id
)");

        ctes.Add(@"latest_chat_title AS (
    SELECT chat_id, chat_title, chat_username
    FROM (
        SELECT chat_id,
               chat_title,
               chat_username,
               ROW_NUMBER() OVER (PARTITION BY chat_id ORDER BY timestamp_utc DESC, chat_id DESC) AS rn
        FROM message_scope
    ) t
    WHERE rn = 1
)");

        ctes.Add(@"message_counts AS (
    SELECT chat_id,
           COUNT(1) AS message_count,
           MAX(timestamp_utc) AS last_message_utc
    FROM message_scope
    GROUP BY chat_id
)");

        ctes.Add(@"flagged_counts AS (
    SELECT chat_id, COUNT(1) AS flagged_count
    FROM flagged_scope
    GROUP BY chat_id
)");

        ctes.Add(@"risk_counts AS (
    SELECT chat_id, COUNT(1) AS risk_count
    FROM risk_scope
    GROUP BY chat_id
)");

        return "WITH " + string.Join(",\n", ctes);
    }

    private static AdminUserListResponseDto CreateEmptyResponse(
        int page,
        int pageSize,
        AdminUserListSourceFilterDto sourceFilter,
        IReadOnlyList<string> warnings)
    {
        return new AdminUserListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = 0,
            ReturnedCount = 0,
            SourceFilter = sourceFilter,
            Summary = new AdminUserListSummaryDto
            {
                CurrentBotScopeApplied = false,
                HistoricalVisibilityEnabled = false,
                LegacySchemaMode = true
            },
            Warnings = warnings
        };
    }

    private static string? NormalizeSearchQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static bool MatchesSearch(AdminUserListItemDto item, string? searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return true;

        var normalized = searchQuery.Trim();
        var comparable = normalized.StartsWith('@') ? normalized[1..] : normalized;

        return item.UserId.ToString(CultureInfo.InvariantCulture).Contains(comparable, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(item.Username) && item.Username.Contains(comparable, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(item.FirstName) && item.FirstName.Contains(normalized, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(item.LastName) && item.LastName.Contains(normalized, StringComparison.OrdinalIgnoreCase)) ||
               BuildCompositeName(item.FirstName, item.LastName).Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCompositeName(string? firstName, string? lastName)
        => string.Join(" ", new[] { firstName, lastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length <= 4)
            return "сохранён";

        var suffix = digits[^4..];
        return $"сохранён, оканчивается на {suffix}";
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
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

    private sealed class UserQuerySchema
    {
        public bool MessagesExists { get; init; }
        public bool MembersExists { get; init; }
        public bool PrivateStartExists { get; init; }
        public bool FlaggedExists { get; init; }
        public bool RiskExists { get; init; }
        public bool ChatRegistryExists { get; init; }
        public bool ChatRegistryHasListed { get; init; }
        public bool ChatRegistryHasTracked { get; init; }
        public bool ChatRegistryHasActive { get; init; }
        public bool ApplyScope { get; init; }
    }
}
