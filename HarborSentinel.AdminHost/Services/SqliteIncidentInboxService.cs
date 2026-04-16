using HarborSentinel.AdminContracts.Incidents;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace HarborSentinel.AdminHost.Services;

public sealed class SqliteIncidentInboxService : IIncidentInboxService
{
    private const string DefaultOperatorId = "default";

    private readonly SqliteIncidentQueryService _incidentQueryService;
    private readonly string _stateDatabasePath;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger<SqliteIncidentInboxService> _log;
    private bool _initialized;

    public SqliteIncidentInboxService(
        SqliteIncidentQueryService incidentQueryService,
        IOptions<HarborRuntimeOptions> runtimeOptions,
        ILogger<SqliteIncidentInboxService> log)
    {
        _incidentQueryService = incidentQueryService;
        _log = log;

        var runtime = runtimeOptions.Value;
        var storageBasePath = Path.GetFullPath(runtime.StorageBasePath);
        var adminStateDir = string.IsNullOrWhiteSpace(runtime.AdminStateDir)
            ? "adminhost_state"
            : runtime.AdminStateDir.Trim();
        _stateDatabasePath = Path.Combine(storageBasePath, adminStateDir, "incident-inbox.db");
    }

    public async Task<AdminIncidentFeedResponseDto> GetIncidentFeedAsync(
        string? operatorId,
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct)
    {
        var normalizedOperatorId = NormalizeOperatorId(operatorId);
        var snapshot = await _incidentQueryService.GetIncidentSnapshotAsync(chatId, recentWindowDays, ct).ConfigureAwait(false);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var hydratedItems = await HydrateOperatorStateAsync(normalizedOperatorId, snapshot.Items, ct).ConfigureAwait(false);
        var pageNumber = Math.Max(page, 1);
        var size = Math.Clamp(pageSize, 10, 200);
        var pagedItems = hydratedItems
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToArray();

        return new AdminIncidentFeedResponseDto
        {
            Page = pageNumber,
            PageSize = size,
            TotalCount = hydratedItems.Count,
            ReturnedCount = pagedItems.Length,
            ChatIdFilter = snapshot.ChatIdFilter,
            RecentWindowDays = snapshot.RecentWindowDays,
            Summary = BuildSummary(snapshot.Summary, hydratedItems),
            Items = pagedItems,
            Warnings = snapshot.Warnings
        };
    }

    public async Task<AdminIncidentStartupSyncResponseDto> GetStartupSyncAsync(
        string? operatorId,
        int recentWindowDays,
        int maxItems,
        CancellationToken ct)
    {
        var normalizedOperatorId = NormalizeOperatorId(operatorId);
        var snapshot = await _incidentQueryService.GetIncidentSnapshotAsync(chatId: null, recentWindowDays, ct).ConfigureAwait(false);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var hydratedItems = await HydrateOperatorStateAsync(normalizedOperatorId, snapshot.Items, ct).ConfigureAwait(false);
        var unreadItems = hydratedItems
            .Where(static item => item.IsUnread)
            .OrderByDescending(static item => item.EventAtUtc)
            .Take(Math.Clamp(maxItems, 1, 50))
            .ToArray();

        return new AdminIncidentStartupSyncResponseDto
        {
            OperatorId = normalizedOperatorId,
            Summary = BuildUnreadSummary(hydratedItems),
            RecentUnreadItems = unreadItems,
            Warnings = snapshot.Warnings
        };
    }

    public async Task<AdminIncidentMarkSeenResponseDto> MarkSeenAsync(
        AdminIncidentMarkSeenRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedOperatorId = NormalizeOperatorId(request.OperatorId);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        IReadOnlyCollection<string> incidentIds;
        if (request.MarkAllMatching)
        {
            var snapshot = await _incidentQueryService.GetIncidentSnapshotAsync(request.ChatIdFilter, request.RecentWindowDays, ct).ConfigureAwait(false);
            incidentIds = snapshot.Items
                .Select(static item => item.IncidentId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            incidentIds = request.IncidentIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var updatedCount = await MarkSeenInternalAsync(normalizedOperatorId, incidentIds, ct).ConfigureAwait(false);
        var sync = await GetStartupSyncAsync(normalizedOperatorId, Math.Clamp(request.RecentWindowDays, 1, 90), 10, ct).ConfigureAwait(false);

        return new AdminIncidentMarkSeenResponseDto
        {
            UpdatedCount = updatedCount,
            Summary = sync.Summary
        };
    }

    public async Task<AdminIncidentAcknowledgeResponseDto> AcknowledgeAsync(
        string incidentId,
        AdminIncidentAcknowledgeRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incidentId))
            throw new ArgumentException("Incident ID is required.", nameof(incidentId));

        ArgumentNullException.ThrowIfNull(request);

        var normalizedOperatorId = NormalizeOperatorId(request.OperatorId);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = OpenStateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO operator_incident_state(
    operator_id,
    incident_id,
    seen_at_utc,
    acknowledged_at_utc,
    updated_at_utc)
VALUES($operator, $incident, $seenAtUtc, $ackAtUtc, $updatedAtUtc)
ON CONFLICT(operator_id, incident_id) DO UPDATE SET
    seen_at_utc = COALESCE(operator_incident_state.seen_at_utc, excluded.seen_at_utc),
    acknowledged_at_utc = COALESCE(operator_incident_state.acknowledged_at_utc, excluded.acknowledged_at_utc),
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$operator", normalizedOperatorId);
        command.Parameters.AddWithValue("$incident", incidentId);
        command.Parameters.AddWithValue("$seenAtUtc", nowUtc);
        command.Parameters.AddWithValue("$ackAtUtc", nowUtc);
        command.Parameters.AddWithValue("$updatedAtUtc", nowUtc);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var states = await GetStatesAsync(normalizedOperatorId, [incidentId], ct).ConfigureAwait(false);
        states.TryGetValue(incidentId, out var state);
        var sync = await GetStartupSyncAsync(normalizedOperatorId, 90, 10, ct).ConfigureAwait(false);

        return new AdminIncidentAcknowledgeResponseDto
        {
            IncidentId = incidentId,
            SeenAtUtc = state?.SeenAtUtc,
            AcknowledgedAtUtc = state?.AcknowledgedAtUtc,
            Summary = sync.Summary
        };
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_stateDatabasePath)!);
            await using var connection = OpenStateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS operator_incident_state(
    operator_id          TEXT NOT NULL,
    incident_id          TEXT NOT NULL,
    seen_at_utc          TEXT,
    acknowledged_at_utc  TEXT,
    updated_at_utc       TEXT NOT NULL,
    PRIMARY KEY(operator_id, incident_id)
);
CREATE INDEX IF NOT EXISTS ix_operator_incident_state_operator_updated
ON operator_incident_state(operator_id, updated_at_utc DESC);";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
            _log.LogInformation("Incident inbox state store ready at {Path}", _stateDatabasePath);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<List<AdminIncidentFeedItemDto>> HydrateOperatorStateAsync(
        string operatorId,
        IReadOnlyList<AdminIncidentFeedItemDto> items,
        CancellationToken ct)
    {
        var states = await GetStatesAsync(operatorId, items.Select(static item => item.IncidentId), ct).ConfigureAwait(false);
        var result = new List<AdminIncidentFeedItemDto>(items.Count);

        foreach (var item in items)
        {
            if (states.TryGetValue(item.IncidentId, out var state))
            {
                item.SeenAtUtc = state.SeenAtUtc;
                item.AcknowledgedAtUtc = state.AcknowledgedAtUtc;
                item.IsUnread = state.SeenAtUtc is null;
                item.IsAcknowledged = state.AcknowledgedAtUtc.HasValue;
            }
            else
            {
                item.SeenAtUtc = null;
                item.AcknowledgedAtUtc = null;
                item.IsUnread = true;
                item.IsAcknowledged = false;
            }

            result.Add(item);
        }

        return result;
    }

    private async Task<Dictionary<string, IncidentStateRecord>> GetStatesAsync(
        string operatorId,
        IEnumerable<string> incidentIds,
        CancellationToken ct)
    {
        var ids = incidentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
            return new Dictionary<string, IncidentStateRecord>(StringComparer.Ordinal);

        await using var connection = OpenStateConnection();
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$operator", operatorId);

        var parameterNames = new List<string>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            var parameterName = $"$id{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, ids[index]);
        }

        command.CommandText = $@"
SELECT incident_id, seen_at_utc, acknowledged_at_utc
FROM operator_incident_state
WHERE operator_id = $operator
  AND incident_id IN ({string.Join(", ", parameterNames)});";

        var result = new Dictionary<string, IncidentStateRecord>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new IncidentStateRecord(
                reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
        }

        return result;
    }

    private async Task<int> MarkSeenInternalAsync(
        string operatorId,
        IReadOnlyCollection<string> incidentIds,
        CancellationToken ct)
    {
        if (incidentIds.Count == 0)
            return 0;

        var seenAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var updatedCount = 0;

        await using var connection = OpenStateConnection();
        foreach (var incidentId in incidentIds)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO operator_incident_state(
    operator_id,
    incident_id,
    seen_at_utc,
    acknowledged_at_utc,
    updated_at_utc)
VALUES($operator, $incident, $seenAtUtc, NULL, $updatedAtUtc)
ON CONFLICT(operator_id, incident_id) DO UPDATE SET
    seen_at_utc = COALESCE(operator_incident_state.seen_at_utc, excluded.seen_at_utc),
    updated_at_utc = excluded.updated_at_utc;";
            command.Parameters.AddWithValue("$operator", operatorId);
            command.Parameters.AddWithValue("$incident", incidentId);
            command.Parameters.AddWithValue("$seenAtUtc", seenAtUtc);
            command.Parameters.AddWithValue("$updatedAtUtc", seenAtUtc);
            updatedCount += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return updatedCount;
    }

    private static AdminIncidentFeedSummaryDto BuildSummary(
        AdminIncidentFeedSummaryDto baseSummary,
        IReadOnlyCollection<AdminIncidentFeedItemDto> items)
    {
        return new AdminIncidentFeedSummaryDto
        {
            DistinctChats = baseSummary.DistinctChats,
            DistinctUsers = baseSummary.DistinctUsers,
            ContextReviewCount = baseSummary.ContextReviewCount,
            HighestRiskScore = baseSummary.HighestRiskScore,
            CurrentScopeIncidents = baseSummary.CurrentScopeIncidents,
            HistoricalOnlyIncidents = baseSummary.HistoricalOnlyIncidents,
            CurrentBotScopeApplied = baseSummary.CurrentBotScopeApplied,
            HistoricalVisibilityEnabled = baseSummary.HistoricalVisibilityEnabled,
            LegacySchemaMode = baseSummary.LegacySchemaMode,
            UnreadCount = items.Count(static item => item.IsUnread),
            AcknowledgedCount = items.Count(static item => item.IsAcknowledged),
            RequiresAttentionCount = items.Count(static item => !item.IsAcknowledged)
        };
    }

    private static AdminIncidentUnreadSummaryDto BuildUnreadSummary(IReadOnlyCollection<AdminIncidentFeedItemDto> items)
    {
        return new AdminIncidentUnreadSummaryDto
        {
            UnreadCount = items.Count(static item => item.IsUnread),
            AcknowledgedCount = items.Count(static item => item.IsAcknowledged),
            RequiresAttentionCount = items.Count(static item => !item.IsAcknowledged),
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string NormalizeOperatorId(string? operatorId)
    {
        if (string.IsNullOrWhiteSpace(operatorId))
            return DefaultOperatorId;

        var value = operatorId.Trim();
        return value.Length <= 128 ? value : value[..128];
    }

    private SqliteConnection OpenStateConnection()
    {
        var connection = new SqliteConnection($"Data Source={_stateDatabasePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private sealed record IncidentStateRecord(DateTimeOffset? SeenAtUtc, DateTimeOffset? AcknowledgedAtUtc);
}
