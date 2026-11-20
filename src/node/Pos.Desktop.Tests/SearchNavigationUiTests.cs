using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views.Dialogs;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>
/// SearchNavigation in the popups: F1 product search, member search and the unit list (no search box).
/// The sales form itself is covered in SalesViewUiTests.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class SearchNavigationUiTests
{
    private static readonly CustomerItem Budi = new(Guid.CreateVersion7(), "C000001", "Budi Pelanggan", "0812", 500000m, 0m, 500000m, 14, 0);
    private static readonly CustomerItem Bunga = new(Guid.CreateVersion7(), "C000002", "Bunga Lestari", "0813", null, 0m, null, null, 0);
    private static readonly CustomerItem Bambang = new(Guid.CreateVersion7(), "C000003", "Bambang", "0814", null, 0m, null, null, 0);

    private static PickerDialogViewModel ProductSearch(string initialQuery = "a") => new(
        "Cari Barang [F1]", WpfDialogService.ProductColumns,
        search: (q, _) => Task.FromResult<IReadOnlyList<object>>(
            new[] { Samples.Amplop, Samples.Kantong, Samples.Voucher }
                .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Sku == q).Cast<object>().ToList()),
        initialQuery: initialQuery, delay: (_, _) => Task.CompletedTask);

    private static PickerDialogViewModel MemberSearch() => new(
        "Cari Member", WpfDialogService.CustomerColumns,
        search: (q, _) => Task.FromResult<IReadOnlyList<object>>(
            new[] { Budi, Bunga, Bambang }.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList()),
        initialQuery: "b", delay: (_, _) => Task.CompletedTask);

    private static void WithPicker(PickerDialogViewModel vm, Action<PickerDialog, TextBox, DataGrid> test) => OnSta(() =>
    {
        var window = ShowOffScreen(new PickerDialog { DataContext = vm });
        try
        {
            DoEvents();
            var grid = Find<DataGrid>(window, "ItemsGrid");
            grid.UpdateLayout();
            test(window, Find<TextBox>(window, "QueryBox"), grid);
        }
        finally
        {
            window.Close();
        }
    });

    private static UIElement Focused(DependencyObject scope) =>
        (UIElement)FocusManager.GetFocusedElement(FocusManager.GetFocusScope(scope))!;

    private static bool IsInside(DependencyObject parent, DependencyObject child)
    {
        for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, parent))
                return true;
        }
        return false;
    }

    [Fact]
    public void F1_popup_down_goes_to_the_first_row_and_enter_chooses_the_highlighted_row() => WithPicker(ProductSearch(), (window, query, grid) =>
    {
        var vm = (PickerDialogViewModel)window.DataContext;
        Assert.True(HasFocus(query));
        Assert.Equal(3, grid.Items.Count);

        PressKey(query, Key.Down);
        Assert.Equal(0, grid.SelectedIndex);
        Assert.True(IsInside(grid, Focused(window)));

        PressKey(Focused(window), Key.Down);
        PressKey(Focused(window), Key.Enter);

        Assert.True(vm.DialogResult);
        Assert.Same(Samples.Kantong, vm.Result);
    });

    [Fact]
    public void F1_popup_up_on_the_top_row_returns_to_the_box_with_caret_at_the_end() => WithPicker(ProductSearch("amplop"), (window, query, grid) =>
    {
        Assert.Single(grid.Items);
        PressKey(query, Key.Down);

        PressKey(Focused(window), Key.Up);

        Assert.True(HasFocus(query));
        Assert.Equal("amplop", query.Text);
        Assert.Equal(6, query.CaretIndex);
        Assert.True(window.IsVisible);
    });

    [Fact]
    public void F1_popup_typing_in_the_grid_searches_again_from_the_box() => WithPicker(ProductSearch(), (window, query, grid) =>
    {
        var vm = (PickerDialogViewModel)window.DataContext;
        PressKey(query, Key.Down);

        foreach (var c in "5113")
            TypeChar(Focused(window), c.ToString());

        Assert.True(HasFocus(query));
        Assert.Equal("5113", query.Text);
        Assert.Equal(4, query.CaretIndex);
        Assert.Same(Samples.Kantong, Assert.Single(grid.Items));
        PressKey(query, Key.Enter);
        Assert.Same(Samples.Kantong, vm.Result);
    });

    [Fact]
    public void Member_search_has_the_same_keys() => WithPicker(MemberSearch(), (window, query, grid) =>
    {
        var vm = (PickerDialogViewModel)window.DataContext;
        Assert.Equal(3, grid.Items.Count);

        PressKey(query, Key.Down);
        PressKey(Focused(window), Key.Down);
        PressKey(Focused(window), Key.Down);
        PressKey(Focused(window), Key.Up);
        PressKey(Focused(window), Key.Up);
        PressKey(Focused(window), Key.Up);                       // top row → back to the box
        Assert.True(HasFocus(query));
        Assert.Equal("b", query.Text);

        PressKey(query, Key.Down);
        PressKey(Focused(window), Key.Down);
        PressKey(Focused(window), Key.Enter);

        Assert.Same(Bunga, vm.Result);
    });

    [Fact]
    public void Unit_list_without_a_search_box_keeps_focus_in_the_grid() => OnSta(() =>
    {
        var vm = new PickerDialogViewModel("Ganti Satuan", WpfDialogService.UnitColumns, [Samples.Amplop, Samples.AmplopPak]);
        var window = ShowOffScreen(new PickerDialog { DataContext = vm });
        try
        {
            var grid = Find<DataGrid>(window, "ItemsGrid");
            grid.UpdateLayout();
            SearchNavigation.FocusRow(grid, 0);

            TypeChar(Focused(window), "x");
            PressKey(Focused(window), Key.Up);
            Assert.True(IsInside(grid, Focused(window)));
            Assert.Equal(0, grid.SelectedIndex);

            PressKey(Focused(window), Key.Down);
            PressKey(Focused(window), Key.Enter);
            Assert.Same(Samples.AmplopPak, vm.Result);
        }
        finally
        {
            window.Close();
        }
    });
}
