using HarborSentinel.AdminContracts.Chats;
using HarborSentinel.AdminContracts.Diagnostics;
using HarborSentinel.AdminContracts.Dashboard;
using HarborSentinel.AdminContracts.Exports;
using HarborSentinel.AdminContracts.Incidents;
using HarborSentinel.AdminContracts.Users;

namespace HarborSentinel.AdminConsole.Services;

public interface IAdminHostClient
{
    Task<AdminDashboardSnapshotDto> GetDashboardSnapshotAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct);

    Task<AdminNodeStatusDto> GetNodeStatusAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct);

    Task<AdminChatListResponseDto> GetChatsAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        int page,
        int pageSize,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter,
        CancellationToken ct);

    Task<AdminChatDetailDto> GetChatDetailAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        long chatId,
        CancellationToken ct);

    Task<AdminIncidentFeedResponseDto> GetIncidentsAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct);

    Task<AdminIncidentStartupSyncResponseDto> GetIncidentStartupSyncAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        int recentWindowDays,
        int maxItems,
        CancellationToken ct);

    Task<AdminIncidentMarkSeenResponseDto> MarkIncidentsSeenAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        AdminIncidentMarkSeenRequestDto request,
        CancellationToken ct);

    Task<AdminIncidentAcknowledgeResponseDto> AcknowledgeIncidentAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        string incidentId,
        AdminIncidentAcknowledgeRequestDto request,
        CancellationToken ct);

    Task<AdminUserListResponseDto> GetUsersAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        int page,
        int pageSize,
        AdminUserListSourceFilterDto sourceFilter,
        string? searchQuery,
        CancellationToken ct);

    Task<AdminUserDetailDto> GetUserDetailAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        long userId,
        CancellationToken ct);

    Task<AdminExportCatalogResponseDto> GetExportCatalogAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct);

    Task<AdminExportJobsResponseDto> GetExportJobsAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct);

    Task<AdminExportJobDetailDto> CreateExportJobAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        AdminExportJobCreateRequestDto request,
        CancellationToken ct);

    Task<AdminExportJobDetailDto> GetExportJobAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        string jobId,
        CancellationToken ct);

    Task<DownloadedAdminExportFile> DownloadExportJobFileAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        string jobId,
        CancellationToken ct);
}
