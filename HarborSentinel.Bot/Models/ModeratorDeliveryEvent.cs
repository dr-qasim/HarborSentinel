namespace HarborSentinel.Bot.Models;

public enum ModeratorDeliveryKind
{
    FlaggedMessage = 0,
    ConversationRisk = 1
}

public sealed class ModeratorDeliveryEvent
{
    public required ModeratorDeliveryKind Kind { get; init; }
    public required MessageLogRecord Record { get; init; }
    public ConversationRiskSnapshot? ConversationRisk { get; init; }
    public string? ChatDisplay { get; init; }
    public string? UserDisplay { get; init; }
    public string? UserLink { get; init; }
    public string? MessageLink { get; init; }
    public string? Caption { get; init; }
    public string? Details { get; init; }
}
