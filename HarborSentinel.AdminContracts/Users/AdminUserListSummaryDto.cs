namespace HarborSentinel.AdminContracts.Users;

public sealed class AdminUserListSummaryDto
{
    public int VisibleUsers { get; init; }
    public int PrivateStartUsers { get; init; }
    public int FlaggedUsers { get; init; }
    public int UsersWithPhone { get; init; }
    public int CurrentScopeUsers { get; init; }
    public int HistoricalOnlyUsers { get; init; }
    public bool CurrentBotScopeApplied { get; init; }
    public bool HistoricalVisibilityEnabled { get; init; }
    public bool LegacySchemaMode { get; init; }
}
