using HarborSentinel.AdminContracts.Incidents;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class IncidentDetailViewModel
{
    public required string IncidentId { get; init; }
    public required string ChatTitle { get; init; }
    public required string ChatId { get; init; }
    public required string UserId { get; init; }
    public required string PersonDisplay { get; init; }
    public required string Username { get; init; }
    public string? UsernameCopyValue { get; init; }
    public required string TimeUtc { get; init; }
    public required string MessageId { get; init; }
    public string? MessageIdCopyValue { get; init; }
    public required string ScopeStatus { get; init; }
    public required string ChatActivity { get; init; }
    public required string Label { get; init; }
    public required string Topic { get; init; }
    public required string Intent { get; init; }
    public required string RiskScore { get; init; }
    public required string Confidence { get; init; }
    public required string NeedsContextReview { get; init; }
    public required string GroupedCount { get; init; }
    public required string MessageText { get; init; }
    public required string Reason { get; init; }
    public required string InboxStatus { get; init; }
    public required string AcknowledgmentStatus { get; init; }
    public required string SeenAtText { get; init; }
    public required string AcknowledgedAtText { get; init; }

    public static IncidentDetailViewModel FromDto(AdminIncidentFeedItemDto item)
    {
        var personDisplay = BuildPersonDisplay(item.FirstName, item.LastName);
        return new IncidentDetailViewModel
        {
            IncidentId = item.IncidentId,
            ChatTitle = string.IsNullOrWhiteSpace(item.ChatTitle) ? "(без названия)" : item.ChatTitle,
            ChatId = item.ChatId.ToString(),
            UserId = item.UserId.ToString(),
            PersonDisplay = personDisplay,
            Username = string.IsNullOrWhiteSpace(item.Username) ? "нет" : $"@{item.Username}",
            UsernameCopyValue = item.Username,
            TimeUtc = ChatPresentationFormatter.FormatTimestamp(item.EventAtUtc),
            MessageId = item.MessageId?.ToString() ?? "нет данных",
            MessageIdCopyValue = item.MessageId?.ToString(),
            ScopeStatus = ChatPresentationFormatter.FormatScopeStatus(item.InCurrentBotScope),
            ChatActivity = ChatPresentationFormatter.FormatActivityState(item.ChatIsActive),
            Label = string.IsNullOrWhiteSpace(item.Label) ? "нет" : item.Label,
            Topic = string.IsNullOrWhiteSpace(item.Topic) ? "нет" : item.Topic,
            Intent = string.IsNullOrWhiteSpace(item.Intent) ? "нет" : item.Intent,
            RiskScore = item.RiskScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Confidence = item.Confidence?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "нет данных",
            NeedsContextReview = item.NeedsContextReview ? "да" : "нет",
            GroupedCount = item.GroupedCount.ToString(),
            MessageText = string.IsNullOrWhiteSpace(item.MessageText) ? "нет данных" : item.MessageText,
            Reason = string.IsNullOrWhiteSpace(item.Reason) ? "нет данных" : item.Reason,
            InboxStatus = item.IsUnread ? "новый" : "просмотрен",
            AcknowledgmentStatus = item.IsAcknowledged ? "подтверждён" : "ожидает подтверждения",
            SeenAtText = item.SeenAtUtc.HasValue ? ChatPresentationFormatter.FormatTimestamp(item.SeenAtUtc.Value) : "ещё не просмотрен",
            AcknowledgedAtText = item.AcknowledgedAtUtc.HasValue ? ChatPresentationFormatter.FormatTimestamp(item.AcknowledgedAtUtc.Value) : "не подтверждён"
        };
    }

    public static IncidentDetailViewModel FromListItem(IncidentListItemViewModel item)
    {
        return new IncidentDetailViewModel
        {
            IncidentId = item.IncidentId,
            ChatTitle = item.ChatTitle,
            ChatId = item.ChatId.ToString(),
            UserId = item.UserId.ToString(),
            PersonDisplay = item.PersonDisplay,
            Username = item.Username,
            UsernameCopyValue = item.UsernameCopyValue,
            TimeUtc = item.DisplayTimeUtc,
            MessageId = item.MessageId?.ToString() ?? "нет данных",
            MessageIdCopyValue = item.MessageId?.ToString(),
            ScopeStatus = item.ScopeStatus,
            ChatActivity = item.ChatActivity,
            Label = item.Label,
            Topic = item.Topic,
            Intent = item.Intent,
            RiskScore = item.RiskScore,
            Confidence = item.Confidence,
            NeedsContextReview = item.NeedsContextReview,
            GroupedCount = item.GroupedCount,
            MessageText = item.MessageText,
            Reason = item.ReasonText,
            InboxStatus = item.InboxStatus,
            AcknowledgmentStatus = item.AcknowledgmentStatus,
            SeenAtText = item.SeenAtText,
            AcknowledgedAtText = item.AcknowledgedAtText
        };
    }

    private static string BuildPersonDisplay(string? firstName, string? lastName)
    {
        var value = string.Join(" ", new[] { firstName, lastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(value) ? "нет" : value;
    }
}
