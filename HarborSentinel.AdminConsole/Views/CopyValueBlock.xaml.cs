using System.Windows;
using System.Windows.Controls;

namespace HarborSentinel.AdminConsole.Views;

public partial class CopyValueBlock : UserControl
{
    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(CopyValueBlock), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CopyValueProperty =
        DependencyProperty.Register(nameof(CopyValue), typeof(object), typeof(CopyValueBlock), new PropertyMetadata(null, OnCopyValueChanged));

    public static readonly DependencyProperty TextWrappingProperty =
        DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(CopyValueBlock), new PropertyMetadata(TextWrapping.NoWrap));

    private static readonly DependencyPropertyKey HasCopyValuePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasCopyValue), typeof(bool), typeof(CopyValueBlock), new PropertyMetadata(false));

    public static readonly DependencyProperty HasCopyValueProperty = HasCopyValuePropertyKey.DependencyProperty;

    public CopyValueBlock()
    {
        InitializeComponent();
        UpdateHasCopyValue();
    }

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public object? CopyValue
    {
        get => GetValue(CopyValueProperty);
        set => SetValue(CopyValueProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public bool HasCopyValue
    {
        get => (bool)GetValue(HasCopyValueProperty);
        private set => SetValue(HasCopyValuePropertyKey, value);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = CopyValue?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        Clipboard.SetText(text);
    }

    private static void OnCopyValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CopyValueBlock)d).UpdateHasCopyValue();
    }

    private void UpdateHasCopyValue()
    {
        HasCopyValue = !string.IsNullOrWhiteSpace(CopyValue?.ToString());
    }
}
