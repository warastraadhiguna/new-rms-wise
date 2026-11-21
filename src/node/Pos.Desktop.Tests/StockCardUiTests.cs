using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Kartu Stok (9b) through real controls: F9 from Stok Total, paging, filters, document detail, export.</summary>
[Collection(WpfUiCollection.Name)]
public class StockCardUiTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 22);
    private static readonly Guid AirId = Guid.CreateVersion7();
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-card-tests", Guid.NewGuid().ToString("N"));

    public StockCardUiTests()
    {
        _api.StockRows.AddRange([
            Stock.Row("AIR-600", "Air mineral 600 ml", 38m, productId: AirId),
            Stock.Row("AIR-600", "Air mineral 600 ml", 3m, Stock.Storage, productId: AirId),
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private StockCardViewModel Card() => new(_api, _dialogs, () => Today);

    private void WithCard(Action<StockCardView, StockCardViewModel> test, Guid? productId = null, Guid? locationId = null) => OnSta(() =>
    {
        var vm = Card();
        var view = new StockCardView { DataContext = vm };
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

    private static List<string> Cells(DataGrid grid, string header, int take = int.MaxValue)
    {
        grid.UpdateLayout();
        var column = grid.Columns.Single(c => (string)c.Header == header);
        return grid.Items.Cast<object>().Take(take).Select(item =>
        {
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            return ((TextBlock)column.GetCellContent(item)).Text;
        }).ToList();
    }

    private MainViewModel Main(params string[] permissions)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith(permissions));
        return new MainViewModel(session, new FormFactories(
            StockTotal: () => new StockTotalViewModel(_api, _dialogs, new MemoryPreferenceStore<StockTotalViewSettings>(new()), (_, _) => Task.CompletedTask),
            StockCheck: () => new StockCheckViewModel(_api, _dialogs),
            StockCard: Card));
    }

    [Fact]
    public void Card_starts_with_the_opening_balance_then_every_movement_with_a_running_balance() => WithCard((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");

        Assert.Equal(["Tanggal", "Jenis Dokumen", "No Dokumen", "Oleh", "Lokasi", "Tanggal Expired", "Masuk", "Keluar", "Saldo"],
            grid.Columns.Select(c => (string)c.Header));
        Assert.Equal(["Saldo Awal", "Penjualan", "Penyesuaian", "Penjualan"], Cells(grid, "Jenis Dokumen"));
        Assert.Equal(["", "20/11/2026 09:00", "20/11/2026 09:01", "20/11/2026 09:02"], Cells(grid, "Tanggal"));   // WIB
        Assert.Equal(["", "TK01-SL-202611-000001", "TK01-ADJ-202611-000002", "TK01-SL-202611-000003"], Cells(grid, "No Dokumen"));
        Assert.Equal(["", "Kasir Satu", "Admin Dev", "Kasir Satu"], Cells(grid, "Oleh"));
        Assert.Equal(["", "01/06/2027", "", "01/06/2027"], Cells(grid, "Tanggal Expired"));
        Assert.Equal(["", "", "5", ""], Cells(grid, "Masuk"));
        Assert.Equal(["", "2", "", "2"], Cells(grid, "Keluar"));
        Assert.Equal(["10", "8", "13", "11"], Cells(grid, "Saldo"));
        Assert.Equal(11m, _api.StockCardData.ClosingBalance);                      // last balance = closing balance
        Assert.Equal("Saldo Awal 10   ·   Masuk 5   ·   Keluar 4   ·   Saldo Akhir 11 PCS", Find<TextBlock>(view, "SummaryText").Text);
        var opening = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.Equal(FontWeights.Bold, opening.FontWeight);
        Assert.Contains("AIR-600 - Air mineral 600 ml", Find<TextBlock>(view, "ProductText").Text);
    }, productId: AirId);

    [Fact]
    public void Defaults_to_this_month_and_the_location_given() => WithCard((view, vm) =>
    {
        Assert.Equal(new DateTime(2026, 11, 1), Find<DatePicker>(view, "FromBox").SelectedDate);
        Assert.Equal(new DateTime(2026, 11, 22), Find<DatePicker>(view, "ToBox").SelectedDate);
        Assert.Equal("STORAGE - Gudang toko", ((LocationOption)Find<ComboBox>(view, "LocationBox").SelectedItem).Display);
        Assert.Contains($"stock-card:{AirId}:{Stock.Storage.Id}:2026-11-01:2026-11-22:0:500", _api.Calls);
    }, productId: AirId, locationId: Stock.Storage.Id);

    [Fact]
    public void Changing_dates_and_location_then_enter_reloads() => WithCard((view, vm) =>
    {
        Find<DatePicker>(view, "FromBox").SelectedDate = new DateTime(2026, 10, 1);
        Find<DatePicker>(view, "ToBox").SelectedDate = new DateTime(2026, 10, 31);
        Find<ComboBox>(view, "LocationBox").SelectedIndex = 1;                     // DISPLAY

        PressKey(Find<DatePicker>(view, "ToBox"), Key.Enter);
        DoEvents();

        Assert.Contains($"stock-card:{AirId}:{Stock.Display.Id}:2026-10-01:2026-10-31:0:500", _api.Calls);
    }, productId: AirId);

    [Fact]
    public void Reversed_dates_are_refused_without_asking_the_server() => WithCard((view, vm) =>
    {
        var calls = _api.Calls.Count(c => c.StartsWith("stock-card", StringComparison.Ordinal));
        Find<DatePicker>(view, "FromBox").SelectedDate = new DateTime(2026, 11, 20);
        Find<DatePicker>(view, "ToBox").SelectedDate = new DateTime(2026, 11, 2);

        Click(Find<Button>(view, "ShowButton"));

        Assert.Equal(calls, _api.Calls.Count(c => c.StartsWith("stock-card", StringComparison.Ordinal)));
        Assert.Equal("Tanggal Sampai lebih awal dari tanggal Dari.", Find<TextBlock>(view, "StatusText").Text);
    }, productId: AirId);

    [Fact]
    public void Thousands_of_rows_load_page_by_page_with_continuous_balances()
    {
        _api.StockCardData = Stock.Card(1200);

        WithCard((view, vm) =>
        {
            var grid = Find<DataGrid>(view, "RowsGrid");
            var more = Find<Button>(view, "LoadMoreButton");
            Assert.Equal(1 + 500, grid.Items.Count);
            Assert.True(more.IsVisible);
            Assert.Equal("Menampilkan 500 dari 1.200 pergerakan", Find<TextBlock>(view, "CountText").Text);

            Click(more);
            Click(more);

            Assert.Equal(1 + 1200, grid.Items.Count);
            Assert.False(more.IsVisible);
            Assert.Contains($"stock-card:{AirId}:all:2026-11-01:2026-11-22:500:500", _api.Calls);
            Assert.Contains($"stock-card:{AirId}:all:2026-11-01:2026-11-22:1000:500", _api.Calls);
            var lines = vm.Rows.ToList();
            for (var i = 1; i < lines.Count; i++)
                Assert.Equal(lines[i - 1].Balance + lines[i].QtyIn - lines[i].QtyOut, lines[i].Balance);
            Assert.Equal(_api.StockCardData.ClosingBalance, lines[^1].Balance);
        }, productId: AirId);
    }

    [Fact]
    public void Double_click_on_a_sale_shows_the_sale_document() => WithCard((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");
        var doubleClick = grid.InputBindings.OfType<MouseBinding>().Single(b => ((MouseGesture)b.Gesture).MouseAction == MouseAction.LeftDoubleClick);
        Assert.Same(vm.OpenDocumentCommand, doubleClick.Command);
        grid.SelectedIndex = 1;                                                    // the sale
        DoEvents();

        doubleClick.Command.Execute(doubleClick.CommandParameter);
        DoEvents();

        Assert.Contains($"get-sale:{Stock.SaleDocId}", _api.Calls);
        var (title, _, lines) = Assert.Single(_dialogs.Documents);
        Assert.Equal("Penjualan TK01-SL-202611-000123", title);
        Assert.Contains(lines, l => l.StartsWith("No     : TK01-SL-202611-000123", StringComparison.Ordinal));
    }, productId: AirId);

    [Fact]
    public void Enter_on_an_adjustment_shows_a_summary_and_the_opening_line_opens_nothing() => WithCard((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "RowsGrid");
        grid.SelectedIndex = 0;
        PressKey(grid, Key.Enter);
        Assert.Empty(_dialogs.Documents);

        grid.SelectedIndex = 2;                                                    // the adjustment
        PressKey(grid, Key.Enter);

        var (title, message, lines) = Assert.Single(_dialogs.Documents);
        Assert.Equal("Penyesuaian TK01-ADJ-202611-000002", title);
        Assert.Contains("belum tersedia", message);
        Assert.Contains("Oleh          : Admin Dev", lines);
        Assert.Contains("Masuk         : 5", lines);
    }, productId: AirId);

    [Fact]
    public void F8_exports_the_opening_line_and_every_movement_across_pages()
    {
        _api.StockCardData = Stock.Card(1200);

        WithCard((view, vm) =>
        {
            Directory.CreateDirectory(_dir);
            _dialogs.SaveFilePath = Path.Combine(_dir, "kartu.xlsx");

            PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F8);
            DoEvents();

            Assert.Contains(_dialogs.Asked, a => a.StartsWith("save-file:Kartu Stok AIR-600 20261101-20261122", StringComparison.Ordinal));
            Assert.Contains($"stock-card:{AirId}:all:2026-11-01:2026-11-22:1000:1000", _api.Calls);
            var rows = XlsxWriterTests.ReadSheet(_dialogs.SaveFilePath);
            Assert.Equal(1 + 1 + 1200, rows.Count);                                // header, Saldo Awal, movements
            Assert.Equal(["Tanggal", "Jenis Dokumen", "No Dokumen", "Oleh", "Lokasi", "Tanggal Expired", "Masuk", "Keluar", "Saldo"],
                rows[0].Select(c => c.Text));
            Assert.Equal(["Saldo Awal", "10"], rows[1].Select(c => c.Text));
            Assert.Equal(["20/11/2026 09:00", "Penjualan", "TK01-SL-202611-000001", "Kasir Satu", "DISPLAY", "01/06/2027", "0", "2", "8"],
                rows[2].Select(c => c.Text));
            Assert.Equal(_api.StockCardData.ClosingBalance.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[^1][^1].Text);
            Assert.Contains("1.200 pergerakan disimpan", Find<TextBlock>(view, "StatusText").Text);
        }, productId: AirId);
    }

    [Fact]
    public void From_the_menu_f1_picks_the_product_and_shows_its_card() => WithCard((view, vm) =>
    {
        Assert.Empty(Find<DataGrid>(view, "RowsGrid").Items);
        _dialogs.ProductToPick = Samples.Amplop;

        PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F1);
        DoEvents();

        Assert.Contains($"stock-card:{Samples.AmplopId}:all:2026-11-01:2026-11-22:0:500", _api.Calls);
        Assert.Equal(4, Find<DataGrid>(view, "RowsGrid").Items.Count);
    });

    [Fact]
    public void F9_on_stok_total_opens_the_kartu_stok_of_the_selected_row_as_a_tab() => OnSta(() =>
    {
        var vm = Main("stock.view");
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            Assert.True(Find<MenuItem>(window, "ReportMenu").IsVisible);
            var tabs = Find<TabControl>(window, "FormTabs");
            tabs.UpdateLayout();
            DoEvents();
            var stockTotal = VisualTreeSearch.Descendants<StockTotalView>(tabs).Single();
            var grid = Find<DataGrid>(stockTotal, "RowsGrid");
            grid.UpdateLayout();
            grid.SelectedIndex = 1;                                                // AIR-600 at STORAGE

            PressKey(Find<TextBox>(stockTotal, "SearchBox"), Key.F9);
            DoEvents();

            var card = Assert.IsType<StockCardViewModel>(vm.SelectedTab);
            Assert.Equal(["Form Stok Total", "Kartu Stok"], vm.Tabs.Select(t => t.Title));
            Assert.Equal(AirId, card.ProductId);
            Assert.Contains($"stock-card:{AirId}:{Stock.Storage.Id}:2026-11-01:2026-11-22:0:500", _api.Calls);

            var menu = Find<MenuItem>(window, "StockCardMenuItem");                  // one Kartu Stok tab is reused
            menu.Command.Execute(null);
            Assert.Equal(2, vm.Tabs.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Cashier_has_no_laporan_menu() => OnSta(() =>
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create"));
        var window = ShowOffScreen(new MainWindow { DataContext = new MainViewModel(session, new FormFactories(StockCard: Card)) });
        try
        {
            Assert.False(Find<MenuItem>(window, "ReportMenu").IsVisible);
            Assert.False(Find<MenuItem>(window, "StockMenu").IsVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
