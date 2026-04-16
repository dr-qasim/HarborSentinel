namespace HarborSentinel.AdminContracts.Users;

public sealed class AdminUserListItemDto
{
    public long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool? InCurrentBotScope { get; init; }
    public bool HasPrivateStart { get; init; }
    public int? PrivateStartCount { get; init; }
    public bool HasPhone { get; init; }
    public int? ChatCount { get; init; }
    public long? MessageCount { get; init; }
    public long? FlaggedEventCount { get; init; }
    public long? RiskEventCount { get; init; }
    public DateTimeOffset? LastActivityUtc { get; init; }
}
