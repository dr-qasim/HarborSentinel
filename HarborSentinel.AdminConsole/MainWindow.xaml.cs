using HarborSentinel.AdminConsole.ViewModels;
using System.Windows;

namespace HarborSentinel.AdminConsole;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
