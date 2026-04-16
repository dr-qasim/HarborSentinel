namespace HarborSentinel.AdminContracts.Users;

public sealed class AdminUserListResponseDto
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int ReturnedCount { get; init; }
    public AdminUserListSourceFilterDto SourceFilter { get; init; }
    public string? SearchQuery { get; init; }
    public required AdminUserListSummaryDto Summary { get; init; }
    public IReadOnlyList<AdminUserListItemDto> Items { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
