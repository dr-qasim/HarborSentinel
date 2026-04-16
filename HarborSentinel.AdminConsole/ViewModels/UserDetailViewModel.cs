using HarborSentinel.AdminContracts.Users;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class UserDetailViewModel
{
    public required long UserId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Username { get; init; } = "нет";
    public string? UsernameCopyValue { get; init; }
    public string FirstName { get; init; } = "нет";
    public string LastName { get; init; } = "нет";
    public string ScopeStatus { get; init; } = "нет данных";
    public string PrivateStartStatus { get; init; } = "нет";
    public string PhoneStatus { get; init; } = "не сохранён";
    public string StartedAtUtc { get; init; } = "нет данных";
    public string LastStartedAtUtc { get; init; } = "нет данных";
    public string StartCount { get; init; } = "нет данных";
    public string ChatCount { get; init; } = "нет данных";
    public string MessageCount { get; init; } = "нет данных";
    public string FlaggedEventCount { get; init; } = "нет данных";
    public string RiskEventCount { get; init; } = "нет данных";
    public string LastActivityUtc { get; init; } = "нет данных";
    public string ScopeMode { get; init; } = "нет данных";
    public IReadOnlyList<UserChatSummaryViewModel> Chats { get; init; } = [];

    public static UserDetailViewModel FromDto(AdminUserDetailDto detail)
    {
        return new UserDetailViewModel
        {
            UserId = detail.UserId,
            DisplayTitle = UserPresentationFormatter.BuildDisplayName(detail.FirstName, detail.LastName, detail.Username, detail.UserId),
            Username = UserPresentationFormatter.FormatUsername(detail.Username),
            UsernameCopyValue = detail.Username,
            FirstName = string.IsNullOrWhiteSpace(detail.FirstName) ? "нет" : detail.FirstName,
            LastName = string.IsNullOrWhiteSpace(detail.LastName) ? "нет" : detail.LastName,
            ScopeStatus = ChatPresentationFormatter.FormatScopeStatus(detail.InCurrentBotScope),
            PrivateStartStatus = UserPresentationFormatter.FormatPrivateStart(detail.HasPrivateStart, detail.StartCount),
            PhoneStatus = string.IsNullOrWhiteSpace(detail.MaskedPhone) ? "не сохранён" : detail.MaskedPhone,
            StartedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.StartedAtUtc),
            LastStartedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.LastStartedAtUtc),
            StartCount = ChatPresentationFormatter.FormatNumber(detail.StartCount),
            ChatCount = ChatPresentationFormatter.FormatNumber(detail.ChatCount),
            MessageCount = ChatPresentationFormatter.FormatNumber(detail.MessageCount),
            FlaggedEventCount = ChatPresentationFormatter.FormatNumber(detail.FlaggedEventCount),
            RiskEventCount = ChatPresentationFormatter.FormatNumber(detail.RiskEventCount),
            LastActivityUtc = ChatPresentationFormatter.FormatTimestamp(detail.LastActivityUtc),
            ScopeMode = detail.CurrentBotScopeApplied
                ? "Показываются текущие и исторические связи; текущий контур определяется по реестру чатов"
                : "Legacy schema: область текущего бота определить невозможно",
            Chats = detail.Chats
                .Select(static item => new UserChatSummaryViewModel
                {
                    ChatId = item.ChatId,
                    DisplayTitle = string.IsNullOrWhiteSpace(item.ChatTitle) ? "(без названия)" : item.ChatTitle,
                    Username = UserPresentationFormatter.FormatUsername(item.ChatUsername),
                    UsernameCopyValue = item.ChatUsername,
                    ScopeStatus = ChatPresentationFormatter.FormatScopeStatus(item.InCurrentBotScope),
                    Tracked = ChatPresentationFormatter.FormatNullableBool(item.IsTracked),
                    Active = ChatPresentationFormatter.FormatNullableBool(item.IsActive),
                    MessageCount = ChatPresentationFormatter.FormatNumber(item.MessageCount),
                    FlaggedEventCount = ChatPresentationFormatter.FormatNumber(item.FlaggedEventCount),
                    RiskEventCount = ChatPresentationFormatter.FormatNumber(item.RiskEventCount),
                    LastSeenUtc = ChatPresentationFormatter.FormatTimestamp(item.LastSeenUtc)
                })
                .ToArray()
        };
    }
}
