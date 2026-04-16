namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentFeedResponseDto
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int ReturnedCount { get; init; }
    public long? ChatIdFilter { get; init; }
    public int RecentWindowDays { get; init; }
    public required AdminIncidentFeedSummaryDto Summary { get; init; }
    public IReadOnlyList<AdminIncidentFeedItemDto> Items { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
