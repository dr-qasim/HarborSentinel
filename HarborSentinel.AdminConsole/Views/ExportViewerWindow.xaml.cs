using System.Windows;

namespace HarborSentinel.AdminConsole.Views;

public partial class ExportViewerWindow : Window
{
    public ExportViewerWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
