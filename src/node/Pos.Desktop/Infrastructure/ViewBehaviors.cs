using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Infrastructure;

/// <summary>
/// Moves keyboard focus from the ViewModel without code-behind: inputs carry <c>FocusBehavior.Key</c>
/// ("search", "qty", ...) and the root binds <c>FocusBehavior.Request</c> to the ViewModel's FocusRequest.
/// </summary>
public static class FocusBehavior
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(FocusBehavior));

    public static readonly DependencyProperty RequestProperty =
        DependencyProperty.RegisterAttached("Request", typeof(FocusRequest), typeof(FocusBehavior),
            new PropertyMetadata(null, OnRequestChanged));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);
    public static FocusRequest? GetRequest(DependencyObject d) => (FocusRequest?)d.GetValue(RequestProperty);
    public static void SetRequest(DependencyObject d, FocusRequest? value) => d.SetValue(RequestProperty, value);

    private static void OnRequestChanged(DependencyObject root, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not FocusRequest request || FindByKey(root, request.Target) is not UIElement target)
            return;

        if (target is FrameworkElement { IsLoaded: false } notLoaded)
        {
            // e.g. the first request of a form that is still being built
            void OnLoaded(object s, RoutedEventArgs a)
            {
                notLoaded.Loaded -= OnLoaded;
                Focus(notLoaded);
            }
            notLoaded.Loaded += OnLoaded;
            return;
        }

        Focus(target);
    }

    private static void Focus(UIElement target)
    {
        target.Focus();
        Keyboard.Focus(target);
        if (target is TextBox box)
            box.SelectAll();
    }

    public static DependencyObject? FindByKey(DependencyObject root, string key)
    {
        if (GetKey(root) == key)
            return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (FindByKey(child, key) is { } found)
                return found;
        }
        return null;
    }
}

/// <summary>
/// Runs a command when a character is typed anywhere inside the element (e.g. "*" for the multiplier,
/// from the numpad or Shift+8) and swallows the character so it does not land in a text box.
/// </summary>
public static class TextInputCommand
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(TextInputCommand),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(TextInputCommand),
            new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
    public static ICommand? GetCommand(DependencyObject d) => (ICommand?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, ICommand? value) => d.SetValue(CommandProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.PreviewTextInput -= OnPreviewTextInput;
            element.PreviewTextInput += OnPreviewTextInput;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var element = (DependencyObject)sender;
        if (e.Text != GetText(element) || GetCommand(element) is not { } command || !command.CanExecute(null))
            return;

        command.Execute(null);
        e.Handled = true;
    }
}

/// <summary>Lets DataGrid columns (not in the visual tree) bind to the DataContext.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>Numbers as RMS 2 shows them: thousands with dots ("37.400"); parameter "qty" keeps up to 4 decimals.</summary>
public sealed class IndonesianNumberConverter : IValueConverter
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        decimal d => d.ToString(parameter as string == "qty" ? "#,0.####" : "#,0", Indonesian),
        null => "",
        _ => value.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed (for DataGrid columns via BindingProxy).</summary>
public sealed class VisibleWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Non-empty string → Visible.</summary>
public sealed class VisibleWhenTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class VisualTreeSearch
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }
}
