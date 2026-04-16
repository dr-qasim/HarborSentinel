namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class UserListItemViewModel
{
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }
    public string Username { get; init; } = "нет";
    public string? UsernameCopyValue { get; init; }
    public string ScopeStatus { get; init; } = "нет данных";
    public string PrivateStartStatus { get; init; } = "нет";
    public string PhoneStatus { get; init; } = "нет";
    public string ChatCount { get; init; } = "нет данных";
    public string MessageCount { get; init; } = "нет данных";
    public string FlaggedEventCount { get; init; } = "нет данных";
    public string RiskEventCount { get; init; } = "нет данных";
    public string LastActivityUtc { get; init; } = "нет данных";
}
