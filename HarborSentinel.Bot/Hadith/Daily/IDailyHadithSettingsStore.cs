using System;

namespace HarborSentinel.Bot.Hadith.Daily
{
    public interface IDailyHadithSettingsStore
    {
        Task<IReadOnlyList<DailyHadithSettings>> GetAllAsync(CancellationToken ct = default);
        Task<DailyHadithSettings?> GetForChatAsync(long chatId, CancellationToken ct = default);
        Task UpsertAsync(DailyHadithSettings settings, CancellationToken ct = default);
        Task DeleteAsync(long chatId, CancellationToken ct = default);
    }
}
