namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ExportJobListItemViewModel
{
    public required string JobId { get; init; }
    public required string Label { get; init; }
    public required string Status { get; init; }
    public required string ScopeSummary { get; init; }
    public required string RequestedAtUtc { get; init; }
    public string CompletedAtUtc { get; init; } = "нет данных";
    public string ResultSummary { get; init; } = "нет данных";
    public string ErrorSummary { get; init; } = "нет";
    public bool HasDownload { get; init; }
}
