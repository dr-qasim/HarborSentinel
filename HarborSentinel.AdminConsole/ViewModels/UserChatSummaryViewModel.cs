namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class UserChatSummaryViewModel
{
    public required long ChatId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Username { get; init; } = "нет";
    public string? UsernameCopyValue { get; init; }
    public string ScopeStatus { get; init; } = "нет данных";
    public string Tracked { get; init; } = "нет данных";
    public string Active { get; init; } = "нет данных";
    public string MessageCount { get; init; } = "нет данных";
    public string FlaggedEventCount { get; init; } = "нет данных";
    public string RiskEventCount { get; init; } = "нет данных";
    public string LastSeenUtc { get; init; } = "нет данных";
}
