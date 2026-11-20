using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pos.Desktop.Infrastructure;

/// <summary>
/// Keyboard navigation between a search box and its results grid, used by EVERY search (sales form, F1 popup,
/// member search, and any later one). Put it on the search TextBox:
/// <code>infra:SearchNavigation.Results="{Binding ElementName=ResultsGrid}"</code>
/// <list type="bullet">
/// <item>Down in the box → first row of the grid (when it has rows).</item>
/// <item>In the grid: Up/Down move one row; Enter runs the grid's own Enter KeyBinding on the selected row.</item>
/// <item>Up on the first row → back to the box, text kept, caret at the end.</item>
/// <item>Typing a letter, digit or symbol in the grid → focus to the box, which gets that character as new text,
/// so a barcode scanned while the grid has focus still works.</item>
/// </list>
/// Returning to the box after an item is added stays the ViewModel's job (focus requests).
/// </summary>
public static class SearchNavigation
{
    public static readonly DependencyProperty ResultsProperty =
        DependencyProperty.RegisterAttached("Results", typeof(DataGrid), typeof(SearchNavigation),
            new PropertyMetadata(null, OnResultsChanged));

    /// <summary>On the grid: the search box it belongs to (set automatically).</summary>
    private static readonly DependencyProperty SearchBoxProperty =
        DependencyProperty.RegisterAttached("SearchBox", typeof(TextBox), typeof(SearchNavigation), new PropertyMetadata(null));

    public static DataGrid? GetResults(DependencyObject d) => (DataGrid?)d.GetValue(ResultsProperty);
    public static void SetResults(DependencyObject d, DataGrid? value) => d.SetValue(ResultsProperty, value);

    private static void OnResultsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        if (e.OldValue is DataGrid oldGrid)
        {
            oldGrid.PreviewKeyDown -= OnGridPreviewKeyDown;
            oldGrid.PreviewTextInput -= OnGridPreviewTextInput;
            oldGrid.ClearValue(SearchBoxProperty);
        }
        box.PreviewKeyDown -= OnBoxPreviewKeyDown;

        if (e.NewValue is DataGrid grid)
        {
            grid.SetValue(SearchBoxProperty, box);
            grid.PreviewKeyDown += OnGridPreviewKeyDown;
            grid.PreviewTextInput += OnGridPreviewTextInput;
            box.PreviewKeyDown += OnBoxPreviewKeyDown;
        }
    }

    private static void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (GetResults((TextBox)sender) is { IsVisible: true, Items.Count: > 0 } grid)
        {
            FocusRow(grid, 0);
            e.Handled = true;
        }
    }

    private static void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (Keyboard.Modifiers != ModifierKeys.None || grid.Items.Count == 0)
            return;

        var index = grid.SelectedIndex;
        switch (e.Key)
        {
            case Key.Up when index <= 0:
                if (grid.GetValue(SearchBoxProperty) is TextBox { IsVisible: true } box)
                {
                    FocusBox(box);
                    e.Handled = true;
                }
                break;
            case Key.Up:
                FocusRow(grid, index - 1);
                e.Handled = true;
                break;
            case Key.Down:
                FocusRow(grid, Math.Min(index + 1, grid.Items.Count - 1));
                e.Handled = true;
                break;
            case Key.Enter:
                // Run the grid's Enter binding ourselves: DataGrid would first move the selection one row down.
                var binding = grid.InputBindings.OfType<KeyBinding>()
                    .FirstOrDefault(b => b.Key == Key.Enter && b.Modifiers == ModifierKeys.None);
                if (binding?.Command is { } command && command.CanExecute(binding.CommandParameter))
                {
                    command.Execute(binding.CommandParameter);
                    e.Handled = true;
                }
                break;
        }
    }

    private static void OnGridPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || !e.Text.All(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)))
            return;
        if (((DataGrid)sender).GetValue(SearchBoxProperty) is not TextBox { IsVisible: true, IsEnabled: true, IsReadOnly: false } box)
            return;

        // The typed character starts a new search (a barcode scan must not be appended to an old query).
        box.Text = e.Text;
        FocusBox(box);
        e.Handled = true;
    }

    private static void FocusBox(TextBox box)
    {
        box.Focus();
        Keyboard.Focus(box);
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>Selects the row and puts keyboard focus on its first visible cell.</summary>
    public static void FocusRow(DataGrid grid, int index)
    {
        if (index < 0 || index >= grid.Items.Count)
            return;
        var item = grid.Items[index];
        grid.SelectedIndex = index;
        grid.ScrollIntoView(item);
        grid.UpdateLayout();

        var column = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
        if (column is not null)
            grid.CurrentCell = new DataGridCellInfo(item, column);

        if (grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row)
        {
            var cell = column?.GetCellContent(row)?.Parent as DataGridCell;
            if (cell is not null)
                cell.Focus();
            else
                row.Focus();
        }
        else
        {
            grid.Focus();
        }
    }
}
