using System.Windows;

namespace HarborSentinel.AdminConsole.Views;

public partial class ChatCardWindow : Window
{
    public ChatCardWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
