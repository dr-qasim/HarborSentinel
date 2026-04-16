namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatModerationSummaryDto
{
    public long? MessageCount { get; init; }
    public DateTimeOffset? LastMessageUtc { get; init; }
    public int? ParticipantCount { get; init; }
    public int? AdminParticipantCount { get; init; }
    public int? FlaggedUserCount { get; init; }
    public long? FlaggedEventCount { get; init; }
    public long? RecentFlaggedEventCount { get; init; }
    public DateTimeOffset? LastFlaggedUtc { get; init; }
    public long? RiskEventCount { get; init; }
    public long? RecentRiskEventCount { get; init; }
    public DateTimeOffset? LastRiskUtc { get; init; }
    public string? LastRiskLabel { get; init; }
    public string? LastRiskTopic { get; init; }
    public string? LastRiskIntent { get; init; }
    public string? LastRiskReason { get; init; }
    public double? LastRiskScore { get; init; }
}
