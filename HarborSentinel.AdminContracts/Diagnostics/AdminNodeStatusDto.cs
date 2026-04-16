namespace HarborSentinel.AdminContracts.Diagnostics;

public sealed class AdminNodeStatusDto
{
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required string Runtime { get; init; }
    public required string Environment { get; init; }
    public required string ListenAddress { get; init; }
    public required string DatabasePath { get; init; }
    public required string StorageBasePath { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required AdminRuntimeSchemaModeDto SchemaMode { get; init; }
    public bool LocalhostOnly { get; init; }
    public bool ApiTokenRequired { get; init; }
    public bool RuntimeDatabaseReadable { get; init; }
    public int WarningCount { get; init; }
    public int CriticalCount { get; init; }
    public IReadOnlyList<AdminNodeComponentStatusDto> Components { get; init; } = [];
    public IReadOnlyList<AdminNodePathStatusDto> Paths { get; init; } = [];
    public IReadOnlyList<AdminRuntimeTableStatusDto> RuntimeTables { get; init; } = [];
    public IReadOnlyList<AdminNodeCapabilityStatusDto> Capabilities { get; init; } = [];
    public IReadOnlyList<AdminDiagnosticEventDto> RecentIssues { get; init; } = [];
}
