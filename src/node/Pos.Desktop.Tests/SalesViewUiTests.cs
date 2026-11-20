using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>"Form Penjualan" driven through its real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class SalesViewUiTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MemoryCartStore _cartStore = new();
    private readonly MemoryViewSettingsStore _viewStore = new();
    private readonly SessionState _session = new();

    public SalesViewUiTests()
    {
        _session.SetLogin(Samples.LoginWith("sale.create", "sale.line.delete"));
        _api.Catalog.AddRange([Samples.Amplop, Samples.Kantong, Samples.Voucher]);
    }

    private SalesViewModel Vm() => new(_api, _session, _dialogs, _cartStore, _viewStore, (_, _) => Task.CompletedTask);

    /// <summary>Runs on an STA thread with the form shown; disposes the window afterwards.</summary>
    private void WithForm(Action<SalesView, SalesViewModel> test) => OnSta(() =>
    {
        var vm = Vm();
        var view = new SalesView { DataContext = vm };
        var window = Host(view);
        try
        {
            test(view, vm);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Scan(SalesView view, string code, string qty = "1")
    {
        Find<TextBox>(view, "QtyBox").Text = qty;
        var search = Find<TextBox>(view, "SearchBox");
        search.Text = code;
        PressKey(search, Key.Enter);
    }

    [Fact]
    public void Typing_in_cari_filters_the_product_grid() => WithForm((view, _) =>
    {
        Find<TextBox>(view, "SearchBox").Text = "amplop";
        DoEvents();

        var grid = Find<DataGrid>(view, "ProductGrid");
        Assert.Equal(1, grid.Items.Count);
        Assert.Equal("13589", ((ProductItem)grid.Items[0]).Sku);
    });

    [Fact]
    public void Enter_in_cari_adds_to_the_cart_resets_x_and_keeps_focus_for_the_next_scan() => WithForm((view, _) =>
    {
        Scan(view, "13589", qty: "2");
        Scan(view, "5113");

        var cart = Find<DataGrid>(view, "CartGrid");
        Assert.Equal(2, cart.Items.Count);
        Assert.Equal("19.500", Find<TextBlock>(view, "TotalText").Text);
        Assert.Equal("1", Find<TextBox>(view, "QtyBox").Text);
        Assert.Equal("", Find<TextBox>(view, "SearchBox").Text);
        Assert.True(HasFocus(Find<TextBox>(view, "SearchBox")));
    });

    [Fact]
    public void Star_typed_in_cari_starts_the_multiplier_and_the_x_box_sets_the_qty() => WithForm((view, vm) =>
    {
        Scan(view, "13589");
        var search = Find<TextBox>(view, "SearchBox");

        TypeChar(search, "*");

        Assert.True(vm.IsMultiplierMode);
        Assert.Equal("", search.Text);                       // "*" did not land in the box
        var qtyBox = Find<TextBox>(view, "QtyBox");
        Assert.True(HasFocus(qtyBox));

        qtyBox.Text = "12";
        PressKey(qtyBox, Key.Enter);

        Assert.Equal(12m, vm.Lines[0].Qty);
        Assert.Equal("90.000", Find<TextBlock>(view, "TotalText").Text);
        Assert.False(vm.IsMultiplierMode);
    });

    [Fact]
    public void Product_grid_enter_adds_the_selected_row() => WithForm((view, vm) =>
    {
        Find<TextBox>(view, "SearchBox").Text = "a";
        DoEvents();
        var grid = Find<DataGrid>(view, "ProductGrid");
        grid.SelectedItem = vm.Products.Single(p => p.Sku == "5113");

        PressKey(grid, Key.Enter);

        Assert.Equal("5113", Assert.Single(vm.Lines).Code);
    });

    [Fact]
    public void F_keys_work_from_the_search_box() => WithForm((view, vm) =>
    {
        _dialogs.ProductToPick = Samples.Kantong;
        _dialogs.Quantity = 5m;
        var search = Find<TextBox>(view, "SearchBox");

        PressKey(search, Key.F1);
        Assert.Equal("5113", Assert.Single(vm.Lines).Code);

        PressKey(search, Key.F8);
        Assert.Equal(5m, vm.Lines[0].Qty);

        _dialogs.CancelReason = "Pelanggan batal";
        _api.OnCancelCart = (id, reason, total, _) => new CartCancellationResult(id, "TK01-BT-202611-000009", reason, total);
        PressKey(search, Key.F12);
        Assert.Empty(vm.Lines);
        Assert.Equal("0", Find<TextBlock>(view, "TotalText").Text);
        Assert.Equal("Transaksi dibatalkan (TK01-BT-202611-000009).", Find<TextBlock>(view, "StatusText").Text);
    });

    [Fact]
    public void Shift_delete_and_ctrl_shift_o_are_bound_like_rms2() => WithForm((view, vm) =>
    {
        var bindings = view.InputBindings.OfType<KeyBinding>().ToList();

        Assert.Contains(bindings, b => b.Key == Key.Delete && b.Modifiers == ModifierKeys.Shift && ReferenceEquals(b.Command, vm.DeleteLineCommand));
        Assert.Contains(bindings, b => b.Key == Key.O && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && ReferenceEquals(b.Command, vm.UnlockDeleteCommand));
        Assert.Contains(bindings, b => b.Key == Key.F1 && ReferenceEquals(b.Command, vm.OpenProductSearchCommand));
        Assert.Contains(bindings, b => b.Key == Key.F8 && ReferenceEquals(b.Command, vm.ChangeQtyCommand));
        Assert.Contains(bindings, b => b.Key == Key.F12 && ReferenceEquals(b.Command, vm.CancelTransactionCommand));
    });

    [Fact]
    public void Hapus_button_deletes_the_selected_row() => WithForm((view, vm) =>
    {
        Scan(view, "13589");
        Scan(view, "5113");
        Find<DataGrid>(view, "CartGrid").SelectedIndex = 0;

        Click(Find<Button>(view, "DeleteButton"));

        Assert.Equal("5113", Assert.Single(vm.Lines).Code);
        Assert.Equal("4.500", Find<TextBlock>(view, "TotalText").Text);
    });

    [Fact]
    public void Unit_button_in_the_cart_row_changes_the_unit() => WithForm((view, vm) =>
    {
        _api.OnUnits = _ => [Samples.Amplop, Samples.AmplopPak];
        _dialogs.UnitToPick = units => units.Single(u => u.UnitCode == "PAK");
        Scan(view, "13589");
        var cart = Find<DataGrid>(view, "CartGrid");
        cart.UpdateLayout();

        var unitButton = VisualTreeSearch.Descendants<Button>(cart).Single(b => (string)b.Content == "▾");
        Click(unitButton);

        Assert.Equal("PAK", vm.Lines[0].UnitCode);
        Assert.Equal("70.000", Find<TextBlock>(view, "TotalText").Text);
    });

    [Fact]
    public void Atur_tampilan_checkboxes_show_columns_and_are_saved() => WithForm((view, _) =>
    {
        var grid = Find<DataGrid>(view, "ProductGrid");
        DataGridColumn Column(string header) => grid.Columns.Single(c => (string)c.Header == header);
        Assert.Equal(Visibility.Collapsed, Column("Merek").Visibility);
        Assert.Equal(Visibility.Visible, Column("Satuan").Visibility);

        Toggle(Find<CheckBox>(view, "ShowBrandBox"));
        Toggle(Find<CheckBox>(view, "ShowUnitBox"));

        Assert.Equal(Visibility.Visible, Column("Merek").Visibility);
        Assert.Equal(Visibility.Collapsed, Column("Satuan").Visibility);
        Assert.True(_viewStore.Settings.ShowBrand);
        Assert.False(_viewStore.Settings.ShowUnit);
    });

    [Fact]
    public void Member_box_enter_selects_the_customer() => WithForm((view, vm) =>
    {
        _api.Customers.Add(new CustomerItem(Guid.CreateVersion7(), "C000001", "Budi Pelanggan", null, 500000m, 0m, 500000m, 14, 0));
        var member = Find<TextBox>(view, "MemberBox");

        member.Text = "C000001";
        PressKey(member, Key.Enter);

        Assert.Equal("C000001", vm.Customer?.Code);
        Assert.Equal("C000001 - Budi Pelanggan", member.Text);
    });

    [Fact]
    public void Escape_with_items_asks_before_closing() => WithForm((view, vm) =>
    {
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        Scan(view, "13589");

        _dialogs.ConfirmAnswer = false;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.Escape);
        Assert.False(closed);

        _dialogs.ConfirmAnswer = true;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.Escape);
        Assert.True(closed);
    });

    [Fact]
    public void Stock_warning_is_shown_under_the_form() => WithForm((view, _) =>
    {
        _api.Catalog.Add(Samples.Product(Guid.CreateVersion7(), "LOW", "Barang stok tipis", 1000m, stock: 1m));

        Scan(view, "LOW", qty: "2");

        var warning = Find<TextBlock>(view, "WarningText");
        Assert.Equal(Visibility.Visible, warning.Visibility);
        Assert.Contains("Barang stok tipis", warning.Text);
    });
}
