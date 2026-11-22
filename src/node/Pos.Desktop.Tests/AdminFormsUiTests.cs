using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Tahap 2 admin screens: main menu by permission, Form Stok Total, Form Cek Stok, through real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class AdminFormsUiTests : IDisposable
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MemoryPreferenceStore<StockTotalViewSettings> _viewStore = new(new StockTotalViewSettings());
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-admin-tests", Guid.NewGuid().ToString("N"));
    private static readonly Guid AirId = Guid.CreateVersion7();

    public AdminFormsUiTests()
    {
        _api.StockRows.AddRange([
            Stock.Row("AIR-600", "Air mineral 600 ml", 38m, productId: AirId),
            Stock.Row("AIR-600", "Air mineral 600 ml", 3m, Stock.Storage, productId: AirId),
            Stock.Row("GULA-1KG", "Gula pasir 1 kg", -5m, displayUnit: "PCS", conversion: 1m),
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private StockTotalViewModel StockTotal() => new(_api, _dialogs, _viewStore, (_, _) => Task.CompletedTask);

    private StockCheckViewModel StockCheck() => new(_api, _dialogs);

    private MainViewModel Main(params string[] permissions)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith(permissions));
        session.Shift = Samples.Shift();
        return new MainViewModel(session, new FormFactories(
            Sales: () => new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
                Receipts.Service(_api, _dialogs), (_, _) => Task.CompletedTask),
            StockTotal: StockTotal,
            StockCheck: StockCheck));
    }

    private static void WithMain(MainViewModel vm, Action<MainWindow> test) => OnSta(() =>
    {
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            test(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static void WithView(FrameworkElement view, Action test) => OnSta(() =>
    {
        var window = Host(view);
        try
        {
            DoEvents();
            test();
        }
        finally
        {
            window.Close();
        }
    });

    private static UIElement Focused(DependencyObject scope) =>
        (UIElement)FocusManager.GetFocusedElement(FocusManager.GetFocusScope(scope))!;

    private static void ClickMenu(MenuItem item)
    {
        item.Command.Execute(item.CommandParameter);
        DoEvents();
    }

    // ---- main menu ----------------------------------------------------------------------------------

    [Fact]
    public void Cashier_sees_only_penjualan_and_starts_in_the_sales_form() => WithMain(Main("sale.create", "shift.open"), window =>
    {
        Assert.True(Find<MenuItem>(window, "SalesMenu").IsVisible);
        Assert.False(Find<MenuItem>(window, "StockMenu").IsVisible);
        var vm = (MainViewModel)window.DataContext;
        Assert.IsType<SalesViewModel>(Assert.Single(vm.Tabs));
    });

    [Fact]
    public void Store_admin_sees_every_menu_and_stok_total_opens_as_a_tab() => WithMain(Main("sale.create", "stock.view"), window =>
    {
        var vm = (MainViewModel)window.DataContext;
        Assert.True(Find<MenuItem>(window, "SalesMenu").IsVisible);
        Assert.True(Find<MenuItem>(window, "StockMenu").IsVisible);

        ClickMenu(Find<MenuItem>(window, "StockTotalMenuItem"));
        ClickMenu(Find<MenuItem>(window, "StockTotalMenuItem"));                  // already open: selected, not doubled

        Assert.Equal(["Form Penjualan 1", "Form Stok Total"], vm.Tabs.Select(t => t.Title));
        Assert.IsType<StockTotalViewModel>(vm.SelectedTab);
        var tabs = Find<TabControl>(window, "FormTabs");
        tabs.UpdateLayout();
        Assert.Equal(2, tabs.Items.Count);
        Assert.IsType<StockTotalView>(VisualTreeSearch.Descendants<ContentPresenter>(tabs)
            .Select(p => VisualTreeHelper.GetChildrenCount(p) > 0 ? VisualTreeHelper.GetChild(p, 0) : null)
            .OfType<StockTotalView>().Single());
    });

    [Fact]
    public void Warehouse_user_without_sale_create_starts_in_stok_total_and_has_no_penjualan() => WithMain(Main("stock.view"), window =>
    {
        var vm = (MainViewModel)window.DataContext;
        Assert.False(Find<MenuItem>(window, "SalesMenu").IsVisible);
        Assert.True(Find<MenuItem>(window, "StockMenu").IsVisible);
        Assert.IsType<StockTotalViewModel>(Assert.Single(vm.Tabs));

        PressKey(window, Key.F2);                                                  // F2 does nothing without sale.create
        Assert.Single(vm.Tabs);
    });


    [Fact]
    public void Another_cashiers_shift_locks_penjualan_but_stock_screens_work()
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create", "stock.view"));
        session.ForeignShift = Samples.Shift(cashierId: Samples.OtherUserId) with { CashierName = "Kasir Satu" };
        var vm = new MainViewModel(session, new FormFactories(
            Sales: () => throw new InvalidOperationException("Form Penjualan must not open"),
            StockTotal: StockTotal,
            StockCheck: StockCheck));

        WithMain(vm, window =>
        {
            var sales = Find<MenuItem>(window, "SalesMenu");
            Assert.True(sales.IsVisible);
            sales.IsSubmenuOpen = true;
            DoEvents();
            Assert.False(Find<MenuItem>(window, "SalesFormMenuItem").IsEnabled);
            var lockItem = Find<MenuItem>(window, "SalesLockMenuItem");
            Assert.Equal(Visibility.Visible, lockItem.Visibility);
            Assert.Equal("Shift milik Kasir Satu sedang terbuka di PC ini (TK01-SH-202611-000007)", lockItem.Header);
            sales.IsSubmenuOpen = false;

            Assert.IsType<StockTotalViewModel>(Assert.Single(vm.Tabs));             // starts in Stok Total, not in selling
            PressKey(window, Key.F2);
            Assert.Single(vm.Tabs);
            Assert.True(Find<MenuItem>(window, "StockMenu").IsVisible);
        });
    }
    [Fact]
    public void Tab_close_button_closes_the_form_and_the_menu_reopens_it() => WithMain(Main("stock.view"), window =>
    {
        var vm = (MainViewModel)window.DataContext;
        var tabs = Find<TabControl>(window, "FormTabs");
        tabs.UpdateLayout();
        var close = VisualTreeSearch.Descendants<Button>(tabs).Single(b => b.Name == "CloseTabButton");

        Click(close);

        Assert.Empty(vm.Tabs);
        Assert.True(Find<Button>(window, "OpenStockTotalButton").IsVisible);
        ClickMenu(Find<MenuItem>(window, "StockTotalMenuItem"));
        Assert.Single(vm.Tabs);
    });

    // ---- Form Stok Total ----------------------------------------------------------------------------

    private void WithStockTotal(Action<StockTotalView, StockTotalViewModel> test) => OnSta(() =>
    {
        var vm = StockTotal();
        var view = new StockTotalView { DataContext = vm };
        var window = Host(view);
        try
        {
            DoEvents();
            Find<DataGrid>(view, "RowsGrid").UpdateLayout();
            test(view, vm);
        }
        finally
        {
            window.Close();
        }
    });

    private static List<string> CellTexts(DataGrid grid, string header)
    {
        grid.UpdateLayout();
        var column = grid.Columns.Single(c => (string)c.Header == header);
        return grid.Items.Cast<object>().Select(item => ((TextBlock)column.GetCellContent(item)).Text).ToList();
    }

    [Fact]
    public void Stok_total_shows_one_row_per_product_and_location_with_mixed_stock() => WithStockTotal((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");

        Assert.Equal(["Kode Barang", "Nama Barang", "Jenis Barang", "Merek", "Letak Barang", "Lokasi", "Satuan", "Stok"],
            grid.Columns.Select(c => (string)c.Header));
        Assert.Equal(["DISPLAY", "STORAGE", "DISPLAY"], CellTexts(grid, "Lokasi"));
        Assert.Equal(["1 BOX 14 PCS", "3 PCS", "-5"], CellTexts(grid, "Stok"));
        Assert.Equal("3 baris", Find<TextBlock>(view, "CountText").Text);
        Assert.Equal(["Semua Lokasi", "DISPLAY - Display", "STORAGE - Gudang toko"],
            Find<ComboBox>(view, "LocationBox").Items.Cast<LocationOption>().Select(l => l.Display));
        Assert.True(HasFocus(Find<TextBox>(view, "SearchBox")));
    });

    [Fact]
    public void Typing_in_cari_and_choosing_a_location_filter_through_the_server() => WithStockTotal((view, vm) =>
    {
        var search = Find<TextBox>(view, "SearchBox");
        foreach (var c in "gula")
            TypeChar(search, c.ToString());
        DoEvents();

        Assert.Contains("stock-totals:gula:all:0:500", _api.Calls);
        Assert.Equal(["Gula pasir 1 kg"], CellTexts(Find<DataGrid>(view, "RowsGrid"), "Nama Barang"));

        search.Clear();
        Find<ComboBox>(view, "LocationBox").SelectedIndex = 2;                   // STORAGE
        DoEvents();

        Assert.Contains($"stock-totals::{Stock.Storage.Id}:0:500", _api.Calls);
        Assert.Equal(["STORAGE"], CellTexts(Find<DataGrid>(view, "RowsGrid"), "Lokasi"));
    });

    [Fact]
    public void Down_enter_from_cari_opens_cek_stok_for_the_highlighted_row() => WithStockTotal((view, vm) =>
    {
        StockTotalRow? opened = null;
        vm.OpenStockCheckRequested += (_, row) => opened = row;
        var search = Find<TextBox>(view, "SearchBox");

        PressKey(search, Key.Down);
        PressKey(Focused(view), Key.Down);
        PressKey(Focused(view), Key.Enter);

        Assert.Equal((AirId, Stock.Storage.Id), (opened?.ProductId, opened?.LocationId));
    });

    [Fact]
    public void Up_on_the_first_row_returns_to_cari_and_typing_in_the_grid_searches() => WithStockTotal((view, vm) =>
    {
        var search = Find<TextBox>(view, "SearchBox");
        PressKey(search, Key.Down);
        PressKey(Focused(view), Key.Up);
        Assert.True(HasFocus(search));

        PressKey(search, Key.Down);
        TypeChar(Focused(view), "g");
        Assert.True(HasFocus(search));
        Assert.Equal("g", search.Text);
    });

    [Fact]
    public void Atur_tampilan_hides_columns_and_is_saved() => WithStockTotal((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");
        DataGridColumn Column(string header) => grid.Columns.Single(c => (string)c.Header == header);

        Toggle(Find<CheckBox>(view, "ShowBrandBox"));
        Toggle(Find<CheckBox>(view, "ShowShelfBox"));

        Assert.Equal(Visibility.Collapsed, Column("Merek").Visibility);
        Assert.Equal(Visibility.Collapsed, Column("Letak Barang").Visibility);
        Assert.Equal(Visibility.Visible, Column("Jenis Barang").Visibility);
        Assert.Equal(new StockTotalViewSettings(ShowCategory: true, ShowBrand: false, ShowShelf: false, ShowUnit: true), _viewStore.Value);
    });

    [Fact]
    public void Saved_atur_tampilan_is_applied_when_the_form_opens()
    {
        _viewStore.Value = new StockTotalViewSettings(ShowCategory: false, ShowUnit: false);

        WithStockTotal((view, _) =>
        {
            var grid = Find<DataGrid>(view, "RowsGrid");
            Assert.Equal(Visibility.Collapsed, grid.Columns.Single(c => (string)c.Header == "Jenis Barang").Visibility);
            Assert.Equal(Visibility.Collapsed, grid.Columns.Single(c => (string)c.Header == "Satuan").Visibility);
            Assert.False(Find<CheckBox>(view, "ShowCategoryBox").IsChecked);
        });
    }

    [Fact]
    public void F8_exports_every_row_of_the_search_to_xlsx_across_pages() => WithStockTotal((view, vm) =>
    {
        for (var i = 0; i < 1200; i++)
            _api.StockRows.Add(Stock.Row($"BULK-{i:0000}", $"Barang banyak {i:0000}", i));
        Directory.CreateDirectory(_dir);
        _dialogs.SaveFilePath = Path.Combine(_dir, "stok-total.xlsx");

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F8);
        DoEvents();

        Assert.Contains(_dialogs.Asked, a => a.StartsWith("save-file:Stok Total", StringComparison.Ordinal));
        Assert.Contains("stock-totals::all:0:1000", _api.Calls);
        Assert.Contains("stock-totals::all:1000:1000", _api.Calls);
        var rows = XlsxWriterTests.ReadSheet(_dialogs.SaveFilePath);
        Assert.Equal(1 + 1203, rows.Count);
        Assert.Equal(["Kode Barang", "Nama Barang", "Jenis Barang", "Merek", "Letak Barang", "Lokasi", "Satuan", "Stok", "Stok (satuan dasar)"],
            rows[0].Select(c => c.Text));
        Assert.Equal(["AIR-600", "Air mineral 600 ml", "Minuman", "Merek X", "Rak A", "DISPLAY", "PCS", "1 BOX 14 PCS", "38"],
            rows[1].Select(c => c.Text));
        Assert.Contains("1.203 baris disimpan", Find<TextBlock>(view, "StatusText").Text);
        Assert.DoesNotContain(rows[0], c => c.Text.Contains("HPP", StringComparison.Ordinal) || c.Text.Contains("Harga Beli", StringComparison.Ordinal));
    });

    [Fact]
    public void Tampilkan_barang_tanpa_stok_is_off_by_default_and_shows_zero_rows_when_ticked() => WithStockTotal((view, vm) =>
    {
        _api.EmptyStockRows.Add(Stock.Row("SABUN-01", "Sabun mandi batang", 0m, Stock.Storage, displayUnit: "PCS", conversion: 1m));
        var box = Find<CheckBox>(view, "IncludeEmptyBox");
        Assert.Equal("Tampilkan barang tanpa stok", box.Content);
        Assert.False(box.IsChecked);
        Assert.DoesNotContain(_api.Calls, c => c.EndsWith(":empty", StringComparison.Ordinal));

        Toggle(box);
        DoEvents();

        Assert.Contains("stock-totals::all:0:500:empty", _api.Calls);
        var names = CellTexts(Find<DataGrid>(view, "RowsGrid"), "Nama Barang");
        Assert.Contains("Sabun mandi batang", names);
        Assert.Equal("0", CellTexts(Find<DataGrid>(view, "RowsGrid"), "Stok")[names.IndexOf("Sabun mandi batang")]);

        Toggle(box);
        DoEvents();
        Assert.DoesNotContain("Sabun mandi batang", CellTexts(Find<DataGrid>(view, "RowsGrid"), "Nama Barang"));
    });

    [Fact]
    public void Cancelled_export_writes_nothing() => WithStockTotal((view, _) =>
    {
        _dialogs.SaveFilePath = null;

        Click(Find<Button>(view, "ExportButton"));

        Assert.DoesNotContain(_api.Calls, c => c.EndsWith(":1000", StringComparison.Ordinal));
        Assert.Equal(Visibility.Collapsed, Find<TextBlock>(view, "StatusText").Visibility);
    });

    // ---- Form Cek Stok ------------------------------------------------------------------------------

    private void WithStockCheck(Action<StockCheckView, StockCheckViewModel> test, Guid? productId = null, Guid? locationId = null) => OnSta(() =>
    {
        var vm = StockCheck();
        var view = new StockCheckView { DataContext = vm };
        var window = Host(view);
        try
        {
            if (productId is { } id)
                vm.ShowAsync(id, locationId).GetAwaiter().GetResult();
            DoEvents();
            Find<DataGrid>(view, "RowsGrid").UpdateLayout();
            test(view, vm);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Cek_stok_shows_each_expiry_date_with_markers_and_colours() => WithStockCheck((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");

        Assert.Equal("AIR-600 - Air mineral 600 ml", Find<TextBlock>(view, "ProductText").Text);
        Assert.Equal(["19/11/2026", "02/12/2026", "01/06/2027"], CellTexts(grid, "Tanggal Expired"));
        Assert.Equal(["2 PCS", "1 BOX 6 PCS", "2 BOX"], CellTexts(grid, "Stok"));
        Assert.Equal(["(SUDAH EXPIRED)", "(HAMPIR EXPIRED)", ""], CellTexts(grid, "Keterangan"));
        Assert.Equal("Total: 3 BOX 8 PCS", Find<TextBlock>(view, "TotalText").Text);
        Assert.Contains("30 hari", Find<TextBlock>(view, "WarningText").Text);

        var rows = grid.Items.Cast<object>().Select(i => (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(i)).ToList();
        Assert.Equal(Color.FromRgb(0xB0, 0x00, 0x20), ((SolidColorBrush)rows[0].Foreground).Color);   // expired: red
        Assert.Equal(Color.FromRgb(0xB3, 0x5C, 0x00), ((SolidColorBrush)rows[1].Foreground).Color);   // near: orange
        Assert.NotEqual(((SolidColorBrush)rows[0].Foreground).Color, ((SolidColorBrush)rows[2].Foreground).Color);
    }, productId: AirId);

    [Fact]
    public void Cek_stok_keeps_the_location_from_stok_total_and_reloads_when_it_changes() => WithStockCheck((view, vm) =>
    {
        Assert.Contains($"stock-expiry:{AirId}:{Stock.Storage.Id}", _api.Calls);
        var location = Find<ComboBox>(view, "LocationBox");
        Assert.Equal("STORAGE - Gudang toko", ((LocationOption)location.SelectedItem).Display);

        location.SelectedIndex = 0;                                               // Semua Lokasi
        DoEvents();

        Assert.Contains($"stock-expiry:{AirId}:all", _api.Calls);
    }, productId: AirId, locationId: Stock.Storage.Id);

    [Fact]
    public void Cek_stok_from_the_menu_picks_the_product_with_f1() => WithStockCheck((view, vm) =>
    {
        Assert.Empty(Find<DataGrid>(view, "RowsGrid").Items);
        _dialogs.ProductToPick = Samples.Amplop;

        PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F1);
        DoEvents();

        Assert.Contains("pick-product:", _dialogs.Asked);
        Assert.Contains($"stock-expiry:{Samples.AmplopId}:all", _api.Calls);
        Assert.Equal(3, Find<DataGrid>(view, "RowsGrid").Items.Count);
    });

    [Fact]
    public void Cek_stok_f8_exports_the_rows_with_markers() => WithStockCheck((view, vm) =>
    {
        Directory.CreateDirectory(_dir);
        _dialogs.SaveFilePath = Path.Combine(_dir, "cek-stok.xlsx");

        PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F8);

        var rows = XlsxWriterTests.ReadSheet(_dialogs.SaveFilePath);
        Assert.Equal(["Barang", "Lokasi", "Tanggal Expired", "Stok", "Stok (satuan dasar)", "Keterangan"], rows[0].Select(c => c.Text));
        Assert.Equal(["AIR-600 - Air mineral 600 ml", "DISPLAY", "19/11/2026", "2 PCS", "2", "(SUDAH EXPIRED)"], rows[1].Select(c => c.Text));
        Assert.Equal(4, rows.Count);
    }, productId: AirId);

    // ---- Stok Total to Cek Stok in the main window ---------------------------------------------------

    [Fact]
    public void Enter_on_a_stok_total_row_opens_cek_stok_as_a_tab_for_that_product_and_location() => WithMain(Main("stock.view"), window =>
    {
        var vm = (MainViewModel)window.DataContext;
        var tabs = Find<TabControl>(window, "FormTabs");
        tabs.UpdateLayout();
        DoEvents();
        var stockTotal = VisualTreeSearch.Descendants<StockTotalView>(tabs).Single();
        Find<DataGrid>(stockTotal, "RowsGrid").UpdateLayout();
        PressKey(Find<TextBox>(stockTotal, "SearchBox"), Key.Down);

        PressKey(Focused(stockTotal), Key.Enter);
        DoEvents();

        var check = Assert.IsType<StockCheckViewModel>(vm.SelectedTab);
        Assert.Equal(["Form Stok Total", "Form Cek Stok"], vm.Tabs.Select(t => t.Title));
        Assert.Equal(AirId, check.ProductId);
        Assert.Contains($"stock-expiry:{AirId}:{Stock.Display.Id}", _api.Calls);

        ClickMenu(Find<MenuItem>(window, "StockCheckMenuItem"));                  // one Cek Stok tab is reused
        Assert.Equal(2, vm.Tabs.Count);
    });
}
