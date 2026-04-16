namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatDetailDto
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
    public bool CurrentBotScopeApplied { get; init; }
    public bool LegacySchemaMode { get; init; }
    public int RecentWindowDays { get; init; }
    public required AdminChatModerationSummaryDto Moderation { get; init; }
    public required AdminChatVigilanceStateDto Vigilance { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
