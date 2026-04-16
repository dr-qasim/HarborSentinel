using HarborSentinel.AdminContracts.Diagnostics;

namespace HarborSentinel.AdminHost.Services;

public interface INodeDiagnosticsService
{
    Task<AdminNodeStatusDto> GetNodeStatusAsync(CancellationToken ct);
}
