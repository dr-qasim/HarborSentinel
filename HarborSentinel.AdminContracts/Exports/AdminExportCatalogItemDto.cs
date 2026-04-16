namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportCatalogItemDto
{
    public AdminExportKindDto Kind { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public bool RequiresChatId { get; init; }
    public bool IsAvailable { get; init; }
    public string? AvailabilityNote { get; init; }
}
