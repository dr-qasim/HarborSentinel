using HarborSentinel.AdminContracts.Diagnostics;

namespace HarborSentinel.AdminHost.Services;

public interface IRecentDiagnosticEventBuffer
{
    void Record(DateTimeOffset timestampUtc, LogLevel level, string category, string message);
    IReadOnlyList<AdminDiagnosticEventDto> GetRecent(int maxCount);
}
