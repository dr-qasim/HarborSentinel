using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HarborSentinel.AdminConsole.Infrastructure;

/// <summary>
/// Maps any admin status string (ConnectionStatus, TunnelStatus, NodeStatus, etc.)
/// to a colour brush. Errors are evaluated first to prevent false positives when
/// a string contains both a success keyword and a failure keyword.
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ConnectionStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OkBrush      = Freeze(Color.FromRgb(0x10, 0x80, 0x10));
    private static readonly SolidColorBrush LoadingBrush = Freeze(Color.FromRgb(0xC0, 0x70, 0x00));
    private static readonly SolidColorBrush ErrorBrush   = Freeze(Color.FromRgb(0xC0, 0x20, 0x20));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
            return SystemColors.GrayTextBrush;

        // ── Errors (evaluated first — a string may contain both "сохранён" and "не удалось") ──
        if (text.Contains("Ошибка", StringComparison.Ordinal)) return ErrorBrush;
        if (text.StartsWith("Не удалось", StringComparison.Ordinal)) return ErrorBrush;
        if (text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)) return ErrorBrush;
        if (text.Contains("недоступен", StringComparison.OrdinalIgnoreCase)) return ErrorBrush;

        // ── Loading / in-progress ──
        if (text.StartsWith("Выполняется", StringComparison.Ordinal)) return LoadingBrush;
        if (text.StartsWith("Запрашивается", StringComparison.Ordinal)) return LoadingBrush;
        if (text.StartsWith("Автоподключение к", StringComparison.Ordinal)) return LoadingBrush;
        if (text.Contains("Переподключение", StringComparison.OrdinalIgnoreCase)) return LoadingBrush;
        if (text.Contains("Попытка", StringComparison.OrdinalIgnoreCase)) return LoadingBrush;
        // Connected but degraded — amber is less alarming than red for a still-running node
        if (text.Contains("критическ", StringComparison.OrdinalIgnoreCase)) return LoadingBrush;

        // ── OK / success ──
        if (text.StartsWith("Подключение установлено", StringComparison.Ordinal)) return OkBrush;
        if (text.Contains("выполнен", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("обновлен", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("сохранён", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("сохранены", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("активен", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("просмотренн", StringComparison.OrdinalIgnoreCase)) return OkBrush;
        if (text.Contains("подтверждён", StringComparison.OrdinalIgnoreCase)) return OkBrush;

        return SystemColors.GrayTextBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
