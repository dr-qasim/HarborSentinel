namespace HarborSentinel.Bot.Models;

public sealed class AdminHealthSnapshot
{
    public required long TotalMessages { get; init; }
    public DateTimeOffset? LastMessageUtc { get; init; }
    public required int TotalChats { get; init; }
    public required int ActiveChats { get; init; }
    public required int RemovedChats { get; init; }
    public required int TrackedChats { get; init; }
    public required int PrivateStartUsers { get; init; }
    public required long TotalFlaggedEvents { get; init; }
    public required long RecentFlaggedEvents { get; init; }
    public DateTimeOffset? LastFlaggedUtc { get; init; }
    public required long TotalRiskEvents { get; init; }
    public required long RecentRiskEvents { get; init; }
    public DateTimeOffset? LastRiskUtc { get; init; }
    public required int ActiveVigilanceChats { get; init; }
}

public sealed class ChatModerationSummary
{
    public required long ChatId { get; init; }
    public required long MessageCount { get; init; }
    public DateTimeOffset? LastMessageUtc { get; init; }
    public required int ParticipantCount { get; init; }
    public required int FlaggedUserCount { get; init; }
    public required long FlaggedEventCount { get; init; }
    public required long RecentFlaggedEventCount { get; init; }
    public DateTimeOffset? LastFlaggedUtc { get; init; }
    public required long RiskEventCount { get; init; }
    public required long RecentRiskEventCount { get; init; }
    public DateTimeOffset? LastRiskUtc { get; init; }
    public string? LastRiskLabel { get; init; }
    public string? LastRiskTopic { get; init; }
    public string? LastRiskIntent { get; init; }
    public string? LastRiskReason { get; init; }
    public double? LastRiskScore { get; init; }
}

public sealed class IncidentFeedEntry
{
    public required DateTimeOffset EventAtUtc { get; init; }
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public required long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public int? MessageId { get; init; }
    public string? Label { get; init; }
    public string? Topic { get; init; }
    public string? Intent { get; init; }
    public double? Confidence { get; init; }
    public required double RiskScore { get; init; }
    public required bool NeedsContextReview { get; init; }
    public string? MessageText { get; init; }
    public int GroupedCount { get; init; } = 1;
    public string? Reason { get; init; }
}
