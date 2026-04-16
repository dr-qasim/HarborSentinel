namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatListItemDto
{
    public long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public string? ChatType { get; init; }
    public string? Status { get; init; }
    public bool? IsActive { get; init; }
    public bool? IsTracked { get; init; }
    public DateTimeOffset? FirstSeenUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public DateTimeOffset? RemovedUtc { get; init; }
}
