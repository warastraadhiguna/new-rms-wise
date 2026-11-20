using System.Windows;
using System.Windows.Controls;

namespace Pos.Desktop.Infrastructure;

/// <summary>
/// Makes <see cref="PasswordBox.Password"/> bindable (WPF does not allow it for security reasons), so the
/// ViewModel can read and clear it without code-behind:
/// <c>&lt;PasswordBox infra:PasswordBoxBinding.BoundPassword="{Binding Password, UpdateSourceTrigger=PropertyChanged}" /&gt;</c>
/// </summary>
/// <remarks>
/// The control → ViewModel direction uses a class handler for every PasswordBox, registered once. An earlier
/// version subscribed only when the bound value changed; because the initial value ("") equals the default,
/// that never happened and typed passwords never reached the ViewModel.
/// </remarks>
public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxBinding));

    static PasswordBoxBinding() =>
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(OnPasswordChanged));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    /// <summary>ViewModel → control (e.g. clearing the box after a login attempt).</summary>
    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox box && !(bool)box.GetValue(IsUpdatingProperty))
        {
            var value = e.NewValue as string ?? "";
            if (box.Password != value)
                box.Password = value;
        }
    }

    /// <summary>Control → ViewModel, only for boxes that use this binding.</summary>
    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || box.ReadLocalValue(BoundPasswordProperty) == DependencyProperty.UnsetValue)
            return;

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
