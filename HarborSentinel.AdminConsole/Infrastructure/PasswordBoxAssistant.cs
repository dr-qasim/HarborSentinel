using System.Windows;
using System.Windows.Controls;

namespace HarborSentinel.AdminConsole.Infrastructure;

public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxAssistant),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false));

    public static void SetBoundPassword(DependencyObject element, string value)
        => element.SetValue(BoundPasswordProperty, value);

    public static string GetBoundPassword(DependencyObject element)
        => (string)element.GetValue(BoundPasswordProperty);

    public static void SetBindPassword(DependencyObject element, bool value)
        => element.SetValue(BindPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject element)
        => (bool)element.GetValue(BindPasswordProperty);

    private static bool GetUpdatingPassword(DependencyObject element)
        => (bool)element.GetValue(UpdatingPasswordProperty);

    private static void SetUpdatingPassword(DependencyObject element, bool value)
        => element.SetValue(UpdatingPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox passwordBox)
            return;

        passwordBox.PasswordChanged -= HandlePasswordChanged;
        if (!GetUpdatingPassword(passwordBox))
            passwordBox.Password = e.NewValue as string ?? string.Empty;
        passwordBox.PasswordChanged += HandlePasswordChanged;
    }

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox passwordBox)
            return;

        if ((bool)e.OldValue)
            passwordBox.PasswordChanged -= HandlePasswordChanged;

        if ((bool)e.NewValue)
            passwordBox.PasswordChanged += HandlePasswordChanged;
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
            return;

        SetUpdatingPassword(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetUpdatingPassword(passwordBox, false);
    }
}
