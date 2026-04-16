namespace HarborSentinel.AdminContracts.Diagnostics;

public sealed class AdminNodePathStatusDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required string FullPath { get; init; }
    public required AdminDiagnosticSeverityDto Severity { get; init; }
    public bool Exists { get; init; }
    public bool Readable { get; init; }
    public bool? Writable { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? LastWriteUtc { get; init; }
    public required string Summary { get; init; }
}
