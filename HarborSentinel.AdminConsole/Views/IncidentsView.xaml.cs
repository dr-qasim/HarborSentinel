using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HarborSentinel.AdminConsole.ViewModels;

namespace HarborSentinel.AdminConsole.Views;

public partial class IncidentsView : UserControl
{
    public IncidentsView()
    {
        InitializeComponent();
    }

    private void OnIncidentRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataRowSource(e.OriginalSource as DependencyObject))
            return;

        if (DataContext is not IncidentsViewModel vm || vm.SelectedIncident is null)
            return;

        OpenIncidentCard(vm);
    }

    private void OnOpenIncidentCardClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IncidentsViewModel vm || vm.SelectedIncident is null)
            return;

        OpenIncidentCard(vm);
    }

    private void OpenIncidentCard(IncidentsViewModel vm)
    {
        var window = new IncidentCardWindow
        {
            DataContext = vm,
            Owner = Window.GetWindow(this),
            Title = $"Инцидент — {vm.SelectedIncident!.ChatTitle}"
        };
        window.ShowDialog();
    }

    // Checks that the double-click came from a data row, not from a column header or empty area.
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
