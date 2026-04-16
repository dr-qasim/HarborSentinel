namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class IncidentListItemViewModel
{
    public string IncidentId { get; init; } = string.Empty;
    public required DateTimeOffset EventAtUtcValue { get; init; }
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public int? MessageId { get; init; }
    public bool IsUnreadValue { get; init; }
    public bool IsAcknowledgedValue { get; init; }
    public DateTimeOffset? SeenAtUtcValue { get; init; }
    public DateTimeOffset? AcknowledgedAtUtcValue { get; init; }
    public string ScopeStatus { get; init; } = "нет данных";
    public string ChatActivity { get; init; } = "нет данных";
    public string? UsernameCopyValue { get; init; }
    public required double RiskScoreValue { get; init; }
    public double? ConfidenceValue { get; init; }
    public required bool NeedsContextReviewValue { get; init; }
    public required int GroupedCountValue { get; init; }
    public string DisplayTimeUtc { get; init; } = "нет данных";
    public string ChatTitle { get; init; } = "(без названия)";
    public string Username { get; init; } = "нет";
    public string Label { get; init; } = "нет";
    public string Topic { get; init; } = "нет";
    public string Intent { get; init; } = "нет";
    public string RiskScore { get; init; } = "нет данных";
    public string Confidence { get; init; } = "нет данных";
    public string NeedsContextReview { get; init; } = "нет";
    public string GroupedCount { get; init; } = "1";
    public string MessagePreview { get; init; } = "нет данных";
    public string ReasonPreview { get; init; } = "нет данных";
    public string PersonDisplay { get; init; } = "нет";
    public string MessageText { get; init; } = "нет данных";
    public string ReasonText { get; init; } = "нет данных";
    public string InboxStatus { get; init; } = "новый";
    public string AcknowledgmentStatus { get; init; } = "ожидает";
    public string SeenAtText { get; init; } = "ещё не просмотрен";
    public string AcknowledgedAtText { get; init; } = "не подтверждён";

    // Severity bands for visual row highlighting
    public bool IsHighRisk => RiskScoreValue >= 7.0;
    public bool IsMediumRisk => RiskScoreValue is >= 4.0 and < 7.0;
}
