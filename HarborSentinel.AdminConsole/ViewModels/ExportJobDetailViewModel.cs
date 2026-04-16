using HarborSentinel.AdminContracts.Exports;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ExportJobDetailViewModel
{
    public required string JobId { get; init; }
    public required string Label { get; init; }
    public required string Status { get; init; }
    public required string ScopeSummary { get; init; }
    public required string RequestedAtUtc { get; init; }
    public string StartedAtUtc { get; init; } = "нет данных";
    public string CompletedAtUtc { get; init; } = "нет данных";
    public string ResultSummary { get; init; } = "нет данных";
    public string ErrorMessage { get; init; } = "нет";
    public string FileName { get; init; } = "нет";
    public string? FileNameCopyValue { get; init; }
    public string FileSize { get; init; } = "нет данных";
    public string FileCreatedAtUtc { get; init; } = "нет данных";
    public bool CanDownload { get; init; }

    public static ExportJobDetailViewModel FromDto(AdminExportJobDetailDto detail)
    {
        return new ExportJobDetailViewModel
        {
            JobId = detail.JobId,
            Label = detail.Label,
            Status = ExportPresentationFormatter.TranslateStatus(detail.Status),
            ScopeSummary = detail.ScopeSummary,
            RequestedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.RequestedAtUtc),
            StartedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.StartedAtUtc),
            CompletedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.CompletedAtUtc),
            ResultSummary = string.IsNullOrWhiteSpace(detail.ResultSummary) ? "нет данных" : detail.ResultSummary,
            ErrorMessage = string.IsNullOrWhiteSpace(detail.ErrorMessage) ? "нет" : detail.ErrorMessage,
            FileName = detail.File?.FileName ?? "нет",
            FileNameCopyValue = detail.File?.FileName,
            FileSize = detail.File is null ? "нет данных" : ExportPresentationFormatter.FormatFileSize(detail.File.SizeBytes),
            FileCreatedAtUtc = ChatPresentationFormatter.FormatTimestamp(detail.File?.CreatedAtUtc),
            CanDownload = detail.File is not null && detail.Status == AdminExportJobStatusDto.Completed
        };
    }
}
