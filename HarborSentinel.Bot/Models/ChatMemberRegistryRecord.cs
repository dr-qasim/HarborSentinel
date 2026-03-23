namespace HarborSentinel.Bot.Models;

public sealed class ChatMemberRegistryRecord
{
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public required long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool IsBot { get; init; }
    public string? Status { get; init; }
    public bool IsAdmin { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public bool SeenInMessages { get; init; }
    public bool SeenInNewChatMembers { get; init; }
    public bool SeenInChatMemberUpdates { get; init; }
    public bool SeenInAdminSnapshot { get; init; }
}
