using System.Collections.Concurrent;
using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;

public interface IRecentMessagesCache
{
    void Add(MessageLogRecord rec);
    IReadOnlyList<DialogMessage> GetRecent(long chatId, long? userId, TimeSpan window, int maxCount);
}

public sealed class RecentMessagesCache : IRecentMessagesCache
{
    // ключ: (chatId, userId)
    private readonly ConcurrentDictionary<(long chatId, long? userId), ConcurrentQueue<DialogMessage>> _map = new();

    // Жёсткие ограничения, чтобы не разрасталось
    private const int MaxQueuePerKey = 500;

    public void Add(MessageLogRecord rec)
    {
        var key = (rec.ChatId, rec.FromId);
        var q = _map.GetOrAdd(key, _ => new ConcurrentQueue<DialogMessage>());
        q.Enqueue(new DialogMessage
        {
            Timestamp = rec.Timestamp,
            ChatId = rec.ChatId,
            ChatTitle = rec.ChatTitle,
            UserId = rec.FromId,
            Username = rec.FromUsername,
            Text = rec.Text
        });

        // обрежем хвост, если переросло
        while (q.Count > MaxQueuePerKey && q.TryDequeue(out _)) { }
    }

    public IReadOnlyList<DialogMessage> GetRecent(long chatId, long? userId, TimeSpan window, int maxCount)
    {
        if (!_map.TryGetValue((chatId, userId), out var q)) return Array.Empty<DialogMessage>();

        var since = DateTimeOffset.UtcNow - window;
        var list = q.Where(d => d.Timestamp >= since)
                    .OrderBy(d => d.Timestamp)
                    .TakeLast(maxCount)
                    .ToList();
        return list;
    }
}
