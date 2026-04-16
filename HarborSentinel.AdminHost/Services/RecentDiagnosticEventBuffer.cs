using HarborSentinel.AdminContracts.Diagnostics;
using System.Collections.Concurrent;

namespace HarborSentinel.AdminHost.Services;

public sealed class RecentDiagnosticEventBuffer : IRecentDiagnosticEventBuffer
{
    private readonly ConcurrentQueue<DiagnosticEventRecord> _events = new();
    private readonly int _capacity;

    public RecentDiagnosticEventBuffer(int capacity = 200)
    {
        _capacity = Math.Max(capacity, 50);
    }

    public void Record(DateTimeOffset timestampUtc, LogLevel level, string category, string message)
    {
        if (level < LogLevel.Warning || string.IsNullOrWhiteSpace(message))
            return;

        if (!category.StartsWith("HarborSentinel.AdminHost", StringComparison.Ordinal))
            return;

        _events.Enqueue(new DiagnosticEventRecord(timestampUtc, level, category, message.Trim()));
        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<AdminDiagnosticEventDto> GetRecent(int maxCount)
    {
        return _events
            .Reverse()
            .Take(Math.Clamp(maxCount, 1, 100))
            .Select(static item => new AdminDiagnosticEventDto
            {
                TimestampUtc = item.TimestampUtc,
                Severity = item.Level >= LogLevel.Error ? AdminDiagnosticSeverityDto.Critical : AdminDiagnosticSeverityDto.Warning,
                Source = item.Category,
                Message = item.Message
            })
            .ToArray();
    }

    private sealed record DiagnosticEventRecord(
        DateTimeOffset TimestampUtc,
        LogLevel Level,
        string Category,
        string Message);
}
