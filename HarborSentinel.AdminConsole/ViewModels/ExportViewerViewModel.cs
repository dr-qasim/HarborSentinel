namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ExportViewerViewModel
{
    public required string JobId { get; init; }
    public required string JobLabel { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required string ScopeSummary { get; init; }
    public required string DownloadedAt { get; init; }
    public required string FileSize { get; init; }
    public required string ResultSummary { get; init; }
    public required ExportContentBase StructuredContent { get; init; }
    public required string RawJson { get; init; }

    public string Title => $"{JobLabel} — {FileName}";
    public string ContentSummary => $"{StructuredContent.TypeLabel} • {FileSize}";
}
