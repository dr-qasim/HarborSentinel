using System.Windows;

namespace HarborSentinel.AdminConsole.Views;

public partial class UserCardWindow : Window
{
    public UserCardWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
