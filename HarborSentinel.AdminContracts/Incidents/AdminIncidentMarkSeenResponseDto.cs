namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentMarkSeenResponseDto
{
    public int UpdatedCount { get; init; }
    public required AdminIncidentUnreadSummaryDto Summary { get; init; }
}
