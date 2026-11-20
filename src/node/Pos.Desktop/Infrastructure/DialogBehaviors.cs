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
            if (column.Format is not null)
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
