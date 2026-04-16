using HarborSentinel.AdminContracts.Incidents;

namespace HarborSentinel.AdminHost.Services;

public interface IIncidentInboxService
{
    Task<AdminIncidentFeedResponseDto> GetIncidentFeedAsync(
        string? operatorId,
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct);

    Task<AdminIncidentStartupSyncResponseDto> GetStartupSyncAsync(
        string? operatorId,
        int recentWindowDays,
        int maxItems,
        CancellationToken ct);

    Task<AdminIncidentMarkSeenResponseDto> MarkSeenAsync(
        AdminIncidentMarkSeenRequestDto request,
        CancellationToken ct);

    Task<AdminIncidentAcknowledgeResponseDto> AcknowledgeAsync(
        string incidentId,
        AdminIncidentAcknowledgeRequestDto request,
        CancellationToken ct);
}
