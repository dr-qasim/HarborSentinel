namespace HarborSentinel.AdminContracts.Diagnostics;

public sealed class AdminDiagnosticEventDto
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required AdminDiagnosticSeverityDto Severity { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
}
