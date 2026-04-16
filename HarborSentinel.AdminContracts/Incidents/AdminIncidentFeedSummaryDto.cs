namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentFeedSummaryDto
{
    public int DistinctChats { get; init; }
    public int DistinctUsers { get; init; }
    public int ContextReviewCount { get; init; }
    public double? HighestRiskScore { get; init; }
    public int CurrentScopeIncidents { get; init; }
    public int HistoricalOnlyIncidents { get; init; }
    public bool CurrentBotScopeApplied { get; init; }
    public bool HistoricalVisibilityEnabled { get; init; }
    public bool LegacySchemaMode { get; init; }
    public int UnreadCount { get; init; }
    public int AcknowledgedCount { get; init; }
    public int RequiresAttentionCount { get; init; }
}
