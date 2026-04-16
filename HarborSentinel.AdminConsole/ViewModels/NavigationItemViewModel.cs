using HarborSentinel.AdminConsole.Infrastructure;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class NavigationItemViewModel : ObservableObject
{
    private string _label = string.Empty;

    public required string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public required string Caption { get; init; }
    public required PageViewModel Page { get; init; }

    public override string ToString() => Label;
}
