namespace HarborSentinel.AdminContracts.Diagnostics;

public sealed class AdminNodeComponentStatusDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required AdminDiagnosticSeverityDto Severity { get; init; }
    public required string Summary { get; init; }
    public string? Details { get; init; }
}
