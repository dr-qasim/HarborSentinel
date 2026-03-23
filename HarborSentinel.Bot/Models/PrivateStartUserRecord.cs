namespace HarborSentinel.Bot.Models;

public sealed class PrivateStartUserRecord
{
    public required long UserId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
    public string? Phone { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset LastStartedAtUtc { get; init; }
    public required int StartCount { get; init; }
}
