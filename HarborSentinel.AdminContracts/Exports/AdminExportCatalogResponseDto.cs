namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportCatalogResponseDto
{
    public IReadOnlyList<AdminExportCatalogItemDto> Items { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
