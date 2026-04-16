using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HarborSentinel.AdminConsole.ViewModels;

namespace HarborSentinel.AdminConsole.Views;

public partial class ChatsView : UserControl
{
    public ChatsView()
    {
        InitializeComponent();
    }

    private void OnChatRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataRowSource(e.OriginalSource as DependencyObject))
            return;

        if (DataContext is not ChatsViewModel vm || vm.SelectedRow is null)
            return;

        OpenChatCard(vm);
    }

    private void OnOpenChatCardClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatsViewModel vm || vm.SelectedRow is null)
            return;

        OpenChatCard(vm);
    }

    private void OpenChatCard(ChatsViewModel vm)
    {
        var window = new ChatCardWindow
        {
            DataContext = vm,
            Owner = Window.GetWindow(this),
            Title = $"Карточка чата — {vm.SelectedRow!.DisplayTitle}"
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
