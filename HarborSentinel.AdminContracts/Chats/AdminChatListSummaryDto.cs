namespace HarborSentinel.AdminContracts.Chats;

public sealed class AdminChatListSummaryDto
{
    public int VisibleChats { get; init; }
    public int? ActiveChats { get; init; }
    public int? RemovedChats { get; init; }
    public int? TrackedChats { get; init; }
    public bool CurrentBotScopeApplied { get; init; }
    public bool LegacySchemaMode { get; init; }
}
