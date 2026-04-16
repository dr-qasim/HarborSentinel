using HarborSentinel.AdminConsole.Infrastructure;

namespace HarborSentinel.AdminConsole.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string Subtitle { get; }
}
