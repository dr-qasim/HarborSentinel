namespace HarborSentinel.AdminContracts.Dashboard;

public sealed class DashboardMetricDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Hint { get; init; }
}
