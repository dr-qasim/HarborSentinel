namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportJobsResponseDto
{
    public int TotalCount { get; init; }
    public IReadOnlyList<AdminExportJobListItemDto> Items { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
