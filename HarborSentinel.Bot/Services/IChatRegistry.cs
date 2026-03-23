using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;

public interface IChatRegistry
{
    Task InitAsync(CancellationToken ct);
    Task UpsertAsync(ChatRegistryUpdate update, CancellationToken ct);
    Task<ChatRegistryEntry?> GetByChatIdAsync(long chatId, CancellationToken ct);
    Task<bool> SetTrackedAsync(long chatId, bool isTracked, CancellationToken ct);
    Task<int> SetTrackedByFilterAsync(ChatRegistryFilter filter, bool isTracked, CancellationToken ct);
    Task<int> CountAsync(ChatRegistryFilter filter, CancellationToken ct);
    Task<IReadOnlyList<ChatRegistryEntry>> GetPageAsync(
        ChatRegistryFilter filter,
        int page,
        int pageSize,
        CancellationToken ct);
}
