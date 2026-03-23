namespace HarborSentinel.Bot.Services;

public interface ITrackedChatExportService
{
    Task<TrackedChatExportResult> ExportAsync(long chatId, CancellationToken ct);
    Task<int> CountParticipantsAsync(long chatId, CancellationToken ct);
    Task<IReadOnlyList<TrackedParticipantEntry>> GetParticipantsPageAsync(long chatId, int page, int pageSize, CancellationToken ct);
    Task<TrackedParticipantsExportResult> ExportParticipantsAsync(long chatId, CancellationToken ct);

    Task<int> CountFlaggedUsersAsync(long? chatId, CancellationToken ct);
    Task<IReadOnlyList<FlaggedUserEntry>> GetFlaggedUsersPageAsync(long? chatId, int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<UserChatSummaryEntry>> GetUserChatsAsync(long userId, CancellationToken ct);
    Task<UserMessagesExportResult> ExportUserMessagesAsync(long userId, long? chatId, CancellationToken ct);
}

public sealed class TrackedChatExportResult
{
    public required string FilePath { get; init; }
    public required long MessageCount { get; init; }
    public required long ParticipantCount { get; init; }
}

public sealed class TrackedParticipantEntry
{
    public required long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsBot { get; init; }
    public string? Status { get; init; }
    public bool SeenInMessages { get; init; }
    public bool SeenInNewChatMembers { get; init; }
    public bool SeenInChatMemberUpdates { get; init; }
    public bool SeenInAdminSnapshot { get; init; }
    public required long MessageCount { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
}

public sealed class TrackedParticipantsExportResult
{
    public required string FilePath { get; init; }
    public required long ParticipantCount { get; init; }
}

public sealed class FlaggedUserEntry
{
    public required long UserId { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public required long FlagCount { get; init; }
    public required DateTimeOffset LastFlaggedUtc { get; init; }
    public long? LastChatId { get; init; }
    public string? LastChatTitle { get; init; }
    public string? LastLabel { get; init; }
    public double? LastConfidence { get; init; }
}

public sealed class UserChatSummaryEntry
{
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public required long MessageCount { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public required long FlaggedCount { get; init; }
}

public sealed class UserMessagesExportResult
{
    public required string FilePath { get; init; }
    public required long MessageCount { get; init; }
    public required long ChatCount { get; init; }
}
