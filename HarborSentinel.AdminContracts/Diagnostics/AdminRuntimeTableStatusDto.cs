namespace HarborSentinel.AdminContracts.Diagnostics;

public sealed class AdminRuntimeTableStatusDto
{
    public required string TableName { get; init; }
    public required AdminDiagnosticSeverityDto Severity { get; init; }
    public bool Exists { get; init; }
    public IReadOnlyList<string> MissingColumns { get; init; } = [];
    public required string Summary { get; init; }
}
