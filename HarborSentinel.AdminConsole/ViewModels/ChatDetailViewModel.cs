using HarborSentinel.AdminContracts.Chats;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ChatDetailViewModel
{
    public required long ChatId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Username { get; init; } = "нет";
    public string? UsernameCopyValue { get; init; }
    public string ChatType { get; init; } = "нет";
    public string Status { get; init; } = "нет";
    public string Active { get; init; } = "нет данных";
    public string Tracked { get; init; } = "нет данных";
    public string ScopeMode { get; init; } = "нет данных";
    public string FirstSeenUtc { get; init; } = "нет данных";
    public string LastSeenUtc { get; init; } = "нет данных";
    public string RemovedUtc { get; init; } = "нет данных";
    public string MessageCount { get; init; } = "нет данных";
    public string LastMessageUtc { get; init; } = "нет данных";
    public string ParticipantCount { get; init; } = "нет данных";
    public string AdminParticipantCount { get; init; } = "нет данных";
    public string FlaggedUserCount { get; init; } = "нет данных";
    public string FlaggedEventCount { get; init; } = "нет данных";
    public string RecentFlaggedEventCount { get; init; } = "нет данных";
    public string LastFlaggedUtc { get; init; } = "нет данных";
    public string RiskEventCount { get; init; } = "нет данных";
    public string RecentRiskEventCount { get; init; } = "нет данных";
    public string LastRiskUtc { get; init; } = "нет данных";
    public string LastRiskLabel { get; init; } = "нет";
    public string LastRiskTopic { get; init; } = "нет";
    public string LastRiskIntent { get; init; } = "нет";
    public string LastRiskScore { get; init; } = "нет данных";
    public string LastRiskReason { get; init; } = "нет данных";
    public string VigilanceStatus { get; init; } = "нет данных";
    public string HeightenedUntilUtc { get; init; } = "нет данных";
    public string LastTriggeredUtc { get; init; } = "нет данных";
    public string TriggerCount { get; init; } = "нет данных";
    public string VigilanceLabel { get; init; } = "нет";
    public string VigilanceTopic { get; init; } = "нет";
    public string VigilanceRiskLevel { get; init; } = "нет";
    public string VigilanceScore { get; init; } = "нет данных";
    public string VigilanceReason { get; init; } = "нет данных";

    public static ChatDetailViewModel FromDto(AdminChatDetailDto detail)
    {
        var title = string.IsNullOrWhiteSpace(detail.ChatTitle) ? "(без названия)" : detail.ChatTitle;
        var vigilanceStatus = !detail.Vigilance.HasStateRecord
            ? "Состояние не зафиксировано"
            : detail.Vigilance.IsHeightenedNow
                ? "Повышенная бдительность активна"
                : "Состояние зафиксировано, активное окно завершено";

        var scopeMode = detail.CurrentBotScopeApplied
            ? "Показывается только область текущего бота"
            : "Legacy schema: изоляция по текущему боту недоступна";

        return new ChatDetailViewModel
        {
            ChatId = detail.ChatId,
            DisplayTitle = title,
            Username = string.IsNullOrWhiteSpace(detail.ChatUsername) ? "нет" : $"@{detail.ChatUsername}",
            UsernameCopyValue = detail.ChatUsername,
            ChatType = ChatPresentationFormatter.TranslateChatType(detail.ChatType),
            Status = ChatPresentationFormatter.TranslateMemberStatus(detail.Status),
            Active = ChatPresentationFormatter.FormatNullableBool(detail.IsActive),
            Tracked = ChatPresentationFormatter.FormatNullableBool(detail.IsTracked),
            ScopeMode = scopeMode,
            FirstSeenUtc = ChatPresentationFormatter.FormatTimestamp(detail.FirstSeenUtc),
            LastSeenUtc = ChatPresentationFormatter.FormatTimestamp(detail.LastSeenUtc),
            RemovedUtc = ChatPresentationFormatter.FormatTimestamp(detail.RemovedUtc),
            MessageCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.MessageCount),
            LastMessageUtc = ChatPresentationFormatter.FormatTimestamp(detail.Moderation.LastMessageUtc),
            ParticipantCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.ParticipantCount),
            AdminParticipantCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.AdminParticipantCount),
            FlaggedUserCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.FlaggedUserCount),
            FlaggedEventCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.FlaggedEventCount),
            RecentFlaggedEventCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.RecentFlaggedEventCount),
            LastFlaggedUtc = ChatPresentationFormatter.FormatTimestamp(detail.Moderation.LastFlaggedUtc),
            RiskEventCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.RiskEventCount),
            RecentRiskEventCount = ChatPresentationFormatter.FormatNumber(detail.Moderation.RecentRiskEventCount),
            LastRiskUtc = ChatPresentationFormatter.FormatTimestamp(detail.Moderation.LastRiskUtc),
            LastRiskLabel = string.IsNullOrWhiteSpace(detail.Moderation.LastRiskLabel) ? "нет" : detail.Moderation.LastRiskLabel,
            LastRiskTopic = string.IsNullOrWhiteSpace(detail.Moderation.LastRiskTopic) ? "нет" : detail.Moderation.LastRiskTopic,
            LastRiskIntent = string.IsNullOrWhiteSpace(detail.Moderation.LastRiskIntent) ? "нет" : detail.Moderation.LastRiskIntent,
            LastRiskScore = ChatPresentationFormatter.FormatScore(detail.Moderation.LastRiskScore),
            LastRiskReason = string.IsNullOrWhiteSpace(detail.Moderation.LastRiskReason) ? "нет данных" : detail.Moderation.LastRiskReason,
            VigilanceStatus = vigilanceStatus,
            HeightenedUntilUtc = ChatPresentationFormatter.FormatTimestamp(detail.Vigilance.HeightenedUntilUtc),
            LastTriggeredUtc = ChatPresentationFormatter.FormatTimestamp(detail.Vigilance.LastTriggeredAtUtc),
            TriggerCount = ChatPresentationFormatter.FormatNumber(detail.Vigilance.TriggerCount),
            VigilanceLabel = string.IsNullOrWhiteSpace(detail.Vigilance.LastLabel) ? "нет" : detail.Vigilance.LastLabel,
            VigilanceTopic = string.IsNullOrWhiteSpace(detail.Vigilance.LastTopic) ? "нет" : detail.Vigilance.LastTopic,
            VigilanceRiskLevel = string.IsNullOrWhiteSpace(detail.Vigilance.LastRiskLevel) ? "нет" : detail.Vigilance.LastRiskLevel,
            VigilanceScore = ChatPresentationFormatter.FormatScore(detail.Vigilance.LastScore),
            VigilanceReason = string.IsNullOrWhiteSpace(detail.Vigilance.LastReason) ? "нет данных" : detail.Vigilance.LastReason
        };
    }
}
