using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HarborSentinel.AdminConsole.ViewModels;

namespace HarborSentinel.AdminConsole.Views;

public partial class UsersView : UserControl
{
    public UsersView()
    {
        InitializeComponent();
    }

    private void OnUserRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataRowSource(e.OriginalSource as DependencyObject))
            return;

        if (DataContext is not UsersViewModel vm || vm.SelectedRow is null)
            return;

        OpenUserCard(vm);
    }

    private void OnOpenUserCardClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UsersViewModel vm || vm.SelectedRow is null)
            return;

        OpenUserCard(vm);
    }

    private void OpenUserCard(UsersViewModel vm)
    {
        var window = new UserCardWindow
        {
            DataContext = vm,
            Owner = Window.GetWindow(this),
            Title = $"Карточка — {vm.SelectedRow!.DisplayName}"
        };
        window.ShowDialog();
    }

    // Проверяет, что двойной клик пришёл по строке данных, а не по заголовку или пустому месту
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
