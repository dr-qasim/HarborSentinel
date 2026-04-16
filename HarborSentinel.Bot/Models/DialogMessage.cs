namespace HarborSentinel.Bot.Models;

public sealed class DialogMessage
{
    public DateTimeOffset Timestamp { get; init; }
    public long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public int MessageId { get; init; }
    public long? UserId { get; init; }
    public string? Username { get; init; }
    public string? Text { get; init; }
}
