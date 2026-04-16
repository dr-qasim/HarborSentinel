namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentMarkSeenRequestDto
{
    public string OperatorId { get; init; } = string.Empty;
    public bool MarkAllMatching { get; init; }
    public int RecentWindowDays { get; init; } = 30;
    public long? ChatIdFilter { get; init; }
    public IReadOnlyList<string> IncidentIds { get; init; } = [];
}
