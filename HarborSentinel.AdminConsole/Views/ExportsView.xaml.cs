using HarborSentinel.AdminConsole.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HarborSentinel.AdminConsole.Views;

public partial class ExportsView : UserControl
{
    public ExportsView()
    {
        InitializeComponent();
    }

    private async void OnOpenExportViewerClick(object sender, RoutedEventArgs e)
    {
        await OpenViewerAsync().ConfigureAwait(true);
    }

    private async void OnJobsRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataRowSource(e.OriginalSource as DependencyObject))
            return;

        await OpenViewerAsync().ConfigureAwait(true);
    }

    private async Task OpenViewerAsync()
    {
        if (DataContext is not ExportsViewModel vm || !vm.CanOpenViewer)
            return;

        var preview = await vm.OpenSelectedExportViewerAsync(CancellationToken.None).ConfigureAwait(true);
        if (preview is null)
            return;

        var window = new ExportViewerWindow
        {
            DataContext = preview,
            Owner = Window.GetWindow(this),
            Title = $"Выгрузка — {preview.FileName}"
        };
        window.ShowDialog();
    }

    private static bool IsDataRowSource(DependencyObject? source)
    {
        if (source is null)
            return false;

        var current = source;
        while (current is not null)
        {
            if (current is DataGridRow)
                return true;
            if (current is DataGridColumnHeader)
                return false;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
