namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class IncidentSeverityFilterViewModel
{
    public required string Label { get; init; }
    public required string Key { get; init; }  // "all" | "high" | "medium" | "low"
}
