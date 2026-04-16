using System.Windows;

namespace HarborSentinel.AdminConsole.Views;

public partial class IncidentCardWindow : Window
{
    public IncidentCardWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
