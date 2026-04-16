namespace HarborSentinel.AdminContracts.Users;

public sealed class AdminUserDetailDto
{
    public long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool? InCurrentBotScope { get; init; }
    public bool HasPrivateStart { get; init; }
    public string? MaskedPhone { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? LastStartedAtUtc { get; init; }
    public int? StartCount { get; init; }
    public int? ChatCount { get; init; }
    public long? MessageCount { get; init; }
    public long? FlaggedEventCount { get; init; }
    public long? RiskEventCount { get; init; }
    public DateTimeOffset? LastActivityUtc { get; init; }
    public bool CurrentBotScopeApplied { get; init; }
    public bool LegacySchemaMode { get; init; }
    public IReadOnlyList<AdminUserChatSummaryDto> Chats { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
