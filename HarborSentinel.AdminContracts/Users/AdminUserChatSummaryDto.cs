namespace HarborSentinel.AdminContracts.Users;

public sealed class AdminUserChatSummaryDto
{
    public long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public bool? InCurrentBotScope { get; init; }
    public bool? IsTracked { get; init; }
    public bool? IsActive { get; init; }
    public long? MessageCount { get; init; }
    public long? FlaggedEventCount { get; init; }
    public long? RiskEventCount { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
}
