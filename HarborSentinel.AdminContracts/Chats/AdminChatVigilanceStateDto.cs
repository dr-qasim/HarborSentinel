namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatVigilanceStateDto
{
    public bool HasStateRecord { get; init; }
    public bool IsHeightenedNow { get; init; }
    public DateTimeOffset? HeightenedUntilUtc { get; init; }
    public DateTimeOffset? LastTriggeredAtUtc { get; init; }
    public int? TriggerCount { get; init; }
    public string? LastLabel { get; init; }
    public string? LastTopic { get; init; }
    public string? LastRiskLevel { get; init; }
    public string? LastReason { get; init; }
    public double? LastScore { get; init; }
}
