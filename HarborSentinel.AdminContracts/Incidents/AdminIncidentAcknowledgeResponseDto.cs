namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentAcknowledgeResponseDto
{
    public required string IncidentId { get; init; }
    public DateTimeOffset? SeenAtUtc { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public required AdminIncidentUnreadSummaryDto Summary { get; init; }
}
