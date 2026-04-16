namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportJobListItemDto
{
    public required string JobId { get; init; }
    public AdminExportKindDto Kind { get; init; }
    public required string Label { get; init; }
    public required string ScopeSummary { get; init; }
    public AdminExportJobStatusDto Status { get; init; }
    public DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? ResultSummary { get; init; }
    public string? ErrorMessage { get; init; }
    public bool HasDownload { get; init; }
}
