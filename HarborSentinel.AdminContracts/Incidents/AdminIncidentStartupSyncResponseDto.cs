namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentStartupSyncResponseDto
{
    public required string OperatorId { get; init; }
    public required AdminIncidentUnreadSummaryDto Summary { get; init; }
    public IReadOnlyList<AdminIncidentFeedItemDto> RecentUnreadItems { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
