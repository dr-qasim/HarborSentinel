namespace HarborSentinel.Bot.Models;

public enum ChatRegistryFilter
{
    Active,
    Removed,
    Tracked,
    All
}

public sealed class ChatRegistryEntry
{
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public string? ChatType { get; init; }
    public string? Status { get; init; }
    public bool IsActive { get; init; }
    public bool IsTracked { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public DateTimeOffset? RemovedUtc { get; init; }
}

public sealed class ChatRegistryUpdate
{
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public required string ChatType { get; init; }
    public string? Status { get; init; }
    public required bool IsActive { get; init; }
    public required DateTimeOffset SeenAtUtc { get; init; }
}
