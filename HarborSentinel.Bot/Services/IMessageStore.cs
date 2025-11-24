using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;
public interface IMessageStore
{
    Task InitAsync(CancellationToken ct);
    Task AppendAsync(MessageLogRecord rec, CancellationToken ct);
    Task<IReadOnlyList<DialogMessage>> QueryUserMessagesAsync(
        long chatId, long? userId, DateTimeOffset? sinceUtc, DateTimeOffset? untilUtc,
        int maxCount, CancellationToken ct);
}
