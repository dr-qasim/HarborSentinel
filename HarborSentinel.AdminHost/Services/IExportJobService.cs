using HarborSentinel.AdminContracts.Exports;

namespace HarborSentinel.AdminHost.Services;

public interface IExportJobService
{
    Task<AdminExportCatalogResponseDto> GetCatalogAsync(CancellationToken ct);

    Task<AdminExportJobsResponseDto> GetJobsAsync(CancellationToken ct);

    Task<AdminExportJobDetailDto?> GetJobAsync(string jobId, CancellationToken ct);

    Task<AdminExportJobDetailDto> CreateJobAsync(AdminExportJobCreateRequestDto request, CancellationToken ct);

    Task<ExportJobDownloadResult?> OpenDownloadAsync(string jobId, CancellationToken ct);
}
