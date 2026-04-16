using HarborSentinel.AdminContracts.Dashboard;

namespace HarborSentinel.AdminHost.Services;

public interface IRuntimeHealthService
{
    Task<AdminDashboardSnapshotDto> GetSnapshotAsync(CancellationToken ct);
}
