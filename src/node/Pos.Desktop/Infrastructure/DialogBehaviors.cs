using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Infrastructure;

/// <summary>Closes a dialog window when the ViewModel sets DialogResult (modal: sets Window.DialogResult).</summary>
public static class DialogCloser
{
    public static readonly DependencyProperty DialogResultProperty =
        DependencyProperty.RegisterAttached("DialogResult", typeof(bool?), typeof(DialogCloser),
            new PropertyMetadata(null, OnChanged));

    public static bool? GetDialogResult(DependencyObject d) => (bool?)d.GetValue(DialogResultProperty);
    public static void SetDialogResult(DependencyObject d, bool? value) => d.SetValue(DialogResultProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not bool result)
            return;

        try
        {
            window.DialogResult = result;   // closes a modal dialog
        }
        catch (InvalidOperationException)
        {
            window.Close();                 // shown non-modally (tests)
        }
    }
}

/// <summary>
/// RMS 2 form navigation: Enter moves to the next input; Enter on an input marked with <c>SubmitCommand</c>
/// runs that command (e.g. "Keterangan" = save). Read-only fields set IsTabStop=False and are skipped.
/// </summary>
public static class EnterNavigation
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(EnterNavigation),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SubmitCommandProperty =
        DependencyProperty.RegisterAttached("SubmitCommand", typeof(System.Windows.Input.ICommand), typeof(EnterNavigation));

    /// <summary>FocusBehavior.Key of the input that Enter moves to (fixed RMS 2 order; ComboBoxes included).</summary>
    public static readonly DependencyProperty NextProperty =
        DependencyProperty.RegisterAttached("Next", typeof(string), typeof(EnterNavigation));

    public static string? GetNext(DependencyObject d) => (string?)d.GetValue(NextProperty);
    public static void SetNext(DependencyObject d, string? value) => d.SetValue(NextProperty, value);

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static System.Windows.Input.ICommand? GetSubmitCommand(DependencyObject d) => (System.Windows.Input.ICommand?)d.GetValue(SubmitCommandProperty);
    public static void SetSubmitCommand(DependencyObject d, System.Windows.Input.ICommand? value) => d.SetValue(SubmitCommandProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement root)
            return;
        root.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true)
            root.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || e.OriginalSource is not DependencyObject source)
            return;

        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, sender); current = Parent(current))
        {
            if (GetSubmitCommand(current) is { } submit)
            {
                if (submit.CanExecute(null))
                    submit.Execute(null);
                e.Handled = true;
                return;
            }

            if (GetNext(current) is { } nextKey && FocusBehavior.FindByKey((DependencyObject)sender, nextKey) is UIElement next)
            {
                next.Focus();
                System.Windows.Input.Keyboard.Focus(next);
                if (next is System.Windows.Controls.TextBox box)
                    box.SelectAll();
                e.Handled = true;
                return;
            }
        }

        if (source is UIElement element)
        {
            element.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);
}

/// <summary>Builds DataGrid columns from <see cref="PickerColumn"/> definitions.</summary>
public static class PickerColumnsBehavior
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.RegisterAttached("Columns", typeof(IReadOnlyList<PickerColumn>), typeof(PickerColumnsBehavior),
            new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<PickerColumn>? GetColumns(DependencyObject d) => (IReadOnlyList<PickerColumn>?)d.GetValue(ColumnsProperty);
    public static void SetColumns(DependencyObject d, IReadOnlyList<PickerColumn>? value) => d.SetValue(ColumnsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
            return;

        grid.Columns.Clear();
        foreach (var column in e.NewValue as IReadOnlyList<PickerColumn> ?? [])
        {
            var binding = new Binding(column.Path);
            if (column.Format is { } format)
            {
                binding.Converter = new IndonesianNumberConverter();
                binding.ConverterParameter = format;
            }

            var textColumn = new DataGridTextColumn
            {
                Header = column.Header,
                Binding = binding,
                Width = column.Width > 0 ? new DataGridLength(column.Width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            if (column.Format is not null || column.Right)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));
                style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0)));
                textColumn.ElementStyle = style;
            }
            grid.Columns.Add(textColumn);
        }
    }
}
