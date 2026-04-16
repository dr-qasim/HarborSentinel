namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class DashboardMetricCardViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Hint { get; init; }
}
