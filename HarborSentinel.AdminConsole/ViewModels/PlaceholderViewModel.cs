namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class PlaceholderViewModel : PageViewModel
{
    public PlaceholderViewModel(string title, string subtitle, string description, string nextStep)
        : base(title, subtitle)
    {
        Description = description;
        NextStep = nextStep;
    }

    public string Description { get; }
    public string NextStep { get; }
}
