namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ChatListItemViewModel
{
    public required long ChatId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Username { get; init; } = "нет";
    public string? UsernameCopyValue { get; init; }
    public string ChatType { get; init; } = "нет";
    public string Status { get; init; } = "нет";
    public string Active { get; init; } = "нет данных";
    public string Tracked { get; init; } = "нет данных";
    public string FirstSeenUtc { get; init; } = "нет данных";
    public string LastSeenUtc { get; init; } = "нет данных";
    public string RemovedUtc { get; init; } = "нет данных";
}
