namespace HarborSentinel.Bot.Models;

public sealed class ConversationRiskSnapshot
{
    public required long ChatId { get; init; }
    public required long? UserId { get; init; }
    public required double TotalScore { get; init; }
    public required int EventCount { get; init; }
    public required int ParticipantCount { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }
    public DateTimeOffset? LastAlertedAtUtc { get; init; }
}
