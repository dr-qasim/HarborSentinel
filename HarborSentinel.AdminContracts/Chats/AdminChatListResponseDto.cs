namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatListResponseDto
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int ReturnedCount { get; init; }
    public AdminChatActivityFilterDto ActivityFilter { get; init; }
    public AdminChatTrackingFilterDto TrackingFilter { get; init; }
    public required AdminChatListSummaryDto Summary { get; init; }
    public IReadOnlyList<AdminChatListItemDto> Items { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
