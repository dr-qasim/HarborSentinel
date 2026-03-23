namespace HarborSentinel.Bot.Models;

public sealed class ChatVigilanceState
{
    public required long ChatId { get; init; }
    public required DateTimeOffset HeightenedUntilUtc { get; init; }
    public required DateTimeOffset LastTriggeredAtUtc { get; init; }
    public required int TriggerCount { get; init; }
    public string? LastLabel { get; init; }
    public string? LastTopic { get; init; }
    public string? LastRiskLevel { get; init; }
    public string? LastReason { get; init; }
    public double? LastScore { get; init; }

    public bool IsActiveAt(DateTimeOffset nowUtc) => HeightenedUntilUtc > nowUtc;
}
