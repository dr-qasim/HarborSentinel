using HarborSentinel.AdminContracts.Incidents;

namespace HarborSentinel.AdminHost.Services;

public interface IIncidentQueryService
{
    Task<AdminIncidentFeedResponseDto> GetIncidentFeedAsync(
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct);
}
