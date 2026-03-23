using System;
using System.Collections.Generic;

namespace HarborSentinel.Bot.AI;

public sealed class ModerationRequest
{
    public required string Text { get; init; }
    public long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public long? UserId { get; init; }
    public string? Username { get; init; }
    public int MessageId { get; init; }
    public string? ReplyToText { get; init; }
    public bool ChatUnderHeightenedVigilance { get; init; }
    public DateTimeOffset? ChatVigilanceUntilUtc { get; init; }
    public int? ChatVigilanceTriggerCount { get; init; }
    public string? ChatVigilanceLabel { get; init; }
    public string? ChatVigilanceTopic { get; init; }
    public string? ChatVigilanceRiskLevel { get; init; }
    public string? ChatVigilanceReason { get; init; }
    public bool HighSensitivityReview { get; init; }
    public IReadOnlyList<ModerationContextMessage> RecentChatMessages { get; init; } = Array.Empty<ModerationContextMessage>();
    public IReadOnlyList<ModerationContextMessage> RecentUserMessages { get; init; } = Array.Empty<ModerationContextMessage>();
}

public sealed class ModerationContextMessage
{
    public DateTimeOffset Timestamp { get; init; }
    public int MessageId { get; init; }
    public long? UserId { get; init; }
    public string? Username { get; init; }
    public string? Text { get; init; }
}
