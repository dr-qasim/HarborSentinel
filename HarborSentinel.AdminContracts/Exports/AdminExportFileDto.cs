namespace HarborSentinel.AdminContracts.Exports;

public sealed class AdminExportFileDto
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
