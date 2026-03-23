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
    private readonly ConcurrentDictionary<(long chatId, long? userId), CacheBucket> _map = new();

    // Жёсткие ограничения, чтобы не разрасталось
    private const int MaxQueuePerKey = 500;
    private const int CleanupEveryAdds = 512;
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RemoveOnReadPadding = TimeSpan.FromMinutes(10);
    private long _addCounter;

    private sealed class CacheBucket
    {
        public ConcurrentQueue<DialogMessage> Queue { get; } = new();
        public object SyncRoot { get; } = new();
        public long LastSeenUtcTicks;
    }

    public void Add(MessageLogRecord rec)
    {
        var now = DateTimeOffset.UtcNow;
        var nowTicks = now.UtcDateTime.Ticks;
        Enqueue((rec.ChatId, rec.FromId), rec, nowTicks);

        if (rec.FromId.HasValue)
        {
            Enqueue((rec.ChatId, null), rec, nowTicks);
        }

        if (Interlocked.Increment(ref _addCounter) % CleanupEveryAdds == 0)
            CleanupStaleKeys(now);
    }

    public IReadOnlyList<DialogMessage> GetRecent(long chatId, long? userId, TimeSpan window, int maxCount)
    {
        var key = (chatId, userId);
        if (!_map.TryGetValue(key, out var bucket))
            return Array.Empty<DialogMessage>();

        var since = DateTimeOffset.UtcNow - window;
        List<DialogMessage> list;
        long lastSeenTicksSnapshot;
        lock (bucket.SyncRoot)
        {
            lastSeenTicksSnapshot = bucket.LastSeenUtcTicks;
            list = bucket.Queue
                .Where(d => d.Timestamp >= since)
                .OrderBy(d => d.Timestamp)
                .TakeLast(maxCount)
                .ToList();
        }

        if (list.Count == 0)
        {
            var lastSeen = ToDateTimeOffsetUtc(lastSeenTicksSnapshot);
            if (lastSeen < since - RemoveOnReadPadding)
                TryRemoveBucketIfUnchanged(key, bucket, lastSeenTicksSnapshot);
        }

        return list;
    }

    private void CleanupStaleKeys(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - KeyTtl;

        foreach (var pair in _map)
        {
            var key = pair.Key;
            var bucket = pair.Value;
            long lastSeenTicksSnapshot;
            bool shouldRemove;

            lock (bucket.SyncRoot)
            {
                lastSeenTicksSnapshot = bucket.LastSeenUtcTicks;
                var lastSeen = ToDateTimeOffsetUtc(lastSeenTicksSnapshot);
                if (lastSeen >= cutoff)
                    continue;

                shouldRemove = !bucket.Queue.TryPeek(out var newest) || newest.Timestamp < cutoff;
            }

            if (!shouldRemove)
                continue;

            TryRemoveBucketIfUnchanged(key, bucket, lastSeenTicksSnapshot);
        }
    }

    private void TryRemoveBucketIfUnchanged(
        (long chatId, long? userId) key,
        CacheBucket bucket,
        long lastSeenTicksSnapshot)
    {
        lock (bucket.SyncRoot)
        {
            if (bucket.LastSeenUtcTicks != lastSeenTicksSnapshot)
                return;
        }

        _map.TryRemove(new KeyValuePair<(long chatId, long? userId), CacheBucket>(key, bucket));
    }

    private void Enqueue((long chatId, long? userId) key, MessageLogRecord rec, long nowTicks)
    {
        var bucket = _map.GetOrAdd(key, static _ => new CacheBucket());

        lock (bucket.SyncRoot)
        {
            bucket.LastSeenUtcTicks = nowTicks;
            var q = bucket.Queue;
            q.Enqueue(new DialogMessage
            {
                Timestamp = rec.Timestamp,
                ChatId = rec.ChatId,
                ChatTitle = rec.ChatTitle,
                MessageId = rec.MessageId,
                UserId = rec.FromId,
                Username = rec.FromUsername,
                Text = rec.Text
            });

            while (q.Count > MaxQueuePerKey && q.TryDequeue(out _)) { }
        }
    }

    private static DateTimeOffset ToDateTimeOffsetUtc(long utcTicks)
    {
        if (utcTicks <= 0)
            return DateTimeOffset.MinValue;

        return new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc));
    }
}
