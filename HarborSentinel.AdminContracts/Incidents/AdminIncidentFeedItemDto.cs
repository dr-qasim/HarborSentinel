namespace HarborSentinel.AdminContracts.Incidents;

public sealed class AdminIncidentFeedItemDto
{
    public string IncidentId { get; set; } = string.Empty;
    public required DateTimeOffset EventAtUtc { get; init; }
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public bool? InCurrentBotScope { get; init; }
    public bool? ChatIsActive { get; init; }
    public required long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public int? MessageId { get; init; }
    public string? Label { get; init; }
    public string? Topic { get; init; }
    public string? Intent { get; init; }
    public double? Confidence { get; init; }
    public required double RiskScore { get; init; }
    public required bool NeedsContextReview { get; init; }
    public string? MessageText { get; init; }
    public int GroupedCount { get; init; } = 1;
    public string? Reason { get; init; }
    public bool IsUnread { get; set; }
    public bool IsAcknowledged { get; set; }
    public DateTimeOffset? SeenAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}
