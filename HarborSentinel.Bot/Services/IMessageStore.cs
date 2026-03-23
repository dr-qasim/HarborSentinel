using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;
public interface IMessageStore
{
    Task InitAsync(CancellationToken ct);
    Task AppendAsync(MessageLogRecord rec, CancellationToken ct);
    Task RegisterPrivateStartAsync(MessageLogRecord rec, string? phone, CancellationToken ct);
    Task UpdatePrivateStartProfileAsync(MessageLogRecord rec, string? phone, CancellationToken ct);
    Task UpsertChatMemberAsync(ChatMemberRegistryRecord rec, CancellationToken ct);
    Task<int> CountPrivateStartUsersAsync(CancellationToken ct);
    Task<IReadOnlyList<PrivateStartUserRecord>> GetPrivateStartUsersPageAsync(
        int page,
        int pageSize,
        CancellationToken ct);
    Task<IReadOnlyList<PrivateStartUserRecord>> GetPrivateStartUsersAsync(CancellationToken ct);
    Task<AdminHealthSnapshot> GetAdminHealthSnapshotAsync(TimeSpan recentWindow, CancellationToken ct);
    Task<ChatModerationSummary> GetChatModerationSummaryAsync(long chatId, TimeSpan recentWindow, CancellationToken ct);
    Task<int> CountIncidentFeedAsync(long? chatId, TimeSpan recentWindow, CancellationToken ct);
    Task<IReadOnlyList<IncidentFeedEntry>> GetIncidentFeedPageAsync(
        long? chatId,
        int page,
        int pageSize,
        TimeSpan recentWindow,
        CancellationToken ct);
    Task RecordFlaggedEventAsync(MessageLogRecord rec, CancellationToken ct);
    Task RecordRiskEventAsync(MessageLogRecord rec, double riskScore, CancellationToken ct);
    Task<ConversationRiskSnapshot?> GetConversationRiskSnapshotAsync(
        long chatId,
        long? userId,
        TimeSpan window,
        CancellationToken ct);
    Task<bool> TryMarkConversationRiskAlertAsync(
        long chatId,
        long? userId,
        DateTimeOffset nowUtc,
        double totalScore,
        TimeSpan cooldown,
        CancellationToken ct);
    Task<ChatVigilanceState?> GetChatVigilanceStateAsync(long chatId, CancellationToken ct);
    Task UpsertChatVigilanceAsync(
        long chatId,
        DateTimeOffset nowUtc,
        DateTimeOffset heightenedUntilUtc,
        string? label,
        string? topic,
        string? riskLevel,
        string? reason,
        double? score,
        CancellationToken ct);
    Task<IReadOnlyList<DialogMessage>> QueryUserMessagesAsync(
        long chatId, long? userId, DateTimeOffset? sinceUtc, DateTimeOffset? untilUtc,
        int maxCount, CancellationToken ct);
}
