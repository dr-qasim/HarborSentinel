namespace HarborSentinel.AdminContracts.Dashboard;

public sealed class AdminDashboardSnapshotDto
{
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required string Runtime { get; init; }
    public required string Environment { get; init; }
    public required string ListenAddress { get; init; }
    public required string DatabasePath { get; init; }
    public required string StorageBasePath { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public bool DatabaseExists { get; init; }
    public bool StorageExists { get; init; }
    public long? DatabaseSizeBytes { get; init; }
    public bool ApiTokenRequired { get; init; }
    public bool LocalhostOnly { get; init; }
    public IReadOnlyList<DashboardMetricDto> Metrics { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
