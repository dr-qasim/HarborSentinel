namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportJobCreateRequestDto
{
    public AdminExportKindDto Kind { get; init; }
    public long? ChatId { get; init; }
}
