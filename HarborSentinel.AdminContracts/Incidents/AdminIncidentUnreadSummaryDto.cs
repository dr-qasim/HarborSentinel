namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentUnreadSummaryDto
{
    public int UnreadCount { get; init; }
    public int AcknowledgedCount { get; init; }
    public int RequiresAttentionCount { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
}
