using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClosedXML.Excel;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Data Penjualan (10a) through real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class SalesListUiTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakePrinter _printer = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-sales-list-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SalesListViewModel Vm() =>
        new(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer), () => Today, (_, _) => Task.CompletedTask);

    private void WithView(Action<SalesListView, SalesListViewModel> test) => OnSta(() =>
    {
        var vm = Vm();
        vm.Initialization.GetAwaiter().GetResult();
        var view = new SalesListView { DataContext = vm };
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

    private static List<string> Cells(DataGrid grid, string header)
    {
        grid.UpdateLayout();
        var column = grid.Columns.Single(c => (string)c.Header == header);
        return grid.Items.Cast<object>().Select(i => column.GetCellContent(i) is TextBlock t ? t.Text : "").ToList();
    }

    private static UIElement Focused(DependencyObject scope) =>
        (UIElement)FocusManager.GetFocusedElement(FocusManager.GetFocusScope(scope))!;

    [Fact]
    public void Shows_todays_sales_with_the_rms2_columns() => WithView((view, vm) =>
    {
        // (added before the form loads in the other tests; here the rows come with a reload)
        _api.SalesRows.AddRange([SalesRows.Credit(), SalesRows.Cash()]);
        PressKey(Find<Button>(view, "ShowButton"), Key.Enter);
        vm.PendingSearch.GetAwaiter().GetResult();
        DoEvents();

        var grid = Find<DataGrid>(view, "RowsGrid");
        Assert.Equal(("2026-11-22", "2026-11-22"), (_api.SalesQueries[^1].From.ToString("yyyy-MM-dd"), _api.SalesQueries[^1].To.ToString("yyyy-MM-dd")));
        Assert.Equal(["TK01-SL-202611-000102", "TK01-SL-202611-000101"], Cells(grid, "No Nota"));
        Assert.Equal(["22/11/2026", "22/11/2026"], Cells(grid, "Tanggal Nota"));
        Assert.Equal(["10:40", "09:15"], Cells(grid, "Waktu"));                     // WIB
        Assert.Equal(["C000001 - Budi Pelanggan", ""], Cells(grid, "Member"));
        Assert.Equal(["TUNAI + KREDIT", "TUNAI"], Cells(grid, "Cara Bayar"));
        Assert.Equal(["62.500", "20.000"], Cells(grid, "Total"));
        Assert.Equal(["4", ""], Cells(grid, "Diskon (%)"));
        Assert.Equal(["2.500", ""], Cells(grid, "Potongan"));
        Assert.Equal(["60.000", "20.000"], Cells(grid, "Grand Total"));
        Assert.Equal(["Belum Lunas", "Lunas"], Cells(grid, "Status"));
        Assert.Equal(["06/12/2026", ""], Cells(grid, "Jatuh Tempo"));
        Assert.Equal(["45.000", ""], Cells(grid, "Total Tertanggung"));
        Assert.Equal(["ambil besok", ""], Cells(grid, "Keterangan"));
        Assert.Equal("2 nota", Find<TextBlock>(view, "CountText").Text);
    });

    [Fact]
    public void Cari_filters_and_uses_the_standard_search_keys_then_f11_shows_and_reprints_the_nota()
    {
        var cash = SalesRows.Cash();
        _api.SalesRows.AddRange([SalesRows.Credit(), cash, SalesRows.Cash("TK01-SL-202611-000077")]);
        WithView((view, vm) =>
        {
            var search = Find<TextBox>(view, "SearchBox");
            Assert.True(HasFocus(search));
            foreach (var c in "0001")
                TypeChar(search, c.ToString());
            vm.PendingSearch.GetAwaiter().GetResult();
            DoEvents();
            var grid = Find<DataGrid>(view, "RowsGrid");
            Assert.Equal("0001", _api.SalesQueries[^1].Q);
            Assert.Equal(["TK01-SL-202611-000102", "TK01-SL-202611-000101"], Cells(grid, "No Nota"));

            PressKey(search, Key.Down);                                             // first row
            PressKey(Focused(view), Key.Down);                                      // the cash nota
            Assert.Equal(cash.Id, vm.SelectedRow?.Id);

            _dialogs.ChooseDocumentAction = true;
            PressKey(Focused(view), Key.Enter);
            DoEvents();

            Assert.Contains($"get-sale:{cash.Id}", _api.Calls);
            var (title, message, lines) = Assert.Single(_dialogs.Documents);
            Assert.Equal("Penjualan TK01-SL-202611-000123", title);                // the receipt as loaded
            Assert.Equal("Status: Lunas.", message);
            Assert.Contains(lines, l => l.Trim() == "CETAK ULANG");
            Assert.Equal(SalesListViewModel.ReprintAction, _dialogs.DocumentActions[0]);
            Assert.Single(_printer.Jobs);
            Assert.StartsWith("Nota TK01-SL-202611-000123 dicetak ulang", Find<TextBlock>(view, "StatusText").Text);
        });
    }

    [Fact]
    public void F11_without_cetak_ulang_prints_nothing()
    {
        _api.SalesRows.Add(SalesRows.Voided());
        WithView((view, vm) =>
        {
            PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F11);
            DoEvents();

            Assert.Equal("Nota ini sudah di-void.", Assert.Single(_dialogs.Documents).Message);
            Assert.Empty(_printer.Jobs);
        });
    }

    [Fact]
    public void A_cashier_cannot_choose_a_date_before_the_limit()
    {
        _api.SalesEarliestDate = Today.AddDays(-15);
        WithView((view, vm) =>
        {
            Assert.Equal("Tanggal awal yang dapat dilihat kasir hanya sampai 07/11/2026.", Find<TextBlock>(view, "LimitText").Text);
            Assert.Equal(new DateTime(2026, 11, 7), Find<DatePicker>(view, "FromBox").DisplayDateStart);
            var queries = _api.SalesQueries.Count;

            Find<DatePicker>(view, "FromBox").SelectedDate = new DateTime(2026, 11, 1);
            PressKey(Find<Button>(view, "ShowButton"), Key.Enter);
            vm.PendingSearch.GetAwaiter().GetResult();
            DoEvents();

            Assert.Equal(queries, _api.SalesQueries.Count);
            Assert.Equal("Tanggal awal yang dapat dilihat kasir hanya sampai 07/11/2026.", Find<TextBlock>(view, "StatusText").Text);

            Find<DatePicker>(view, "FromBox").SelectedDate = new DateTime(2026, 11, 7);
            PressKey(Find<Button>(view, "ShowButton"), Key.Enter);
            vm.PendingSearch.GetAwaiter().GetResult();

            Assert.Equal(new DateOnly(2026, 11, 7), _api.SalesQueries[^1].From);
        });
    }

    [Fact]
    public void F8_exports_every_nota_with_the_rms2_columns()
    {
        _api.SalesRows.AddRange([SalesRows.Credit(), SalesRows.Cash()]);
        var path = Path.Combine(_dir, "data-penjualan.xlsx");
        Directory.CreateDirectory(_dir);
        _dialogs.SaveFilePath = path;
        WithView((view, vm) =>
        {
            PressKey(Find<DataGrid>(view, "RowsGrid"), Key.F8);
            DoEvents();
            for (var i = 0; i < 50 && !File.Exists(path); i++)
                DoEvents();
        });

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("No Nota", sheet.Cell(1, 1).GetString());
        Assert.Equal("Total Tertanggung", sheet.Cell(1, 13).GetString());
        Assert.Equal("TK01-SL-202611-000102", sheet.Cell(2, 1).GetString());
        Assert.Equal(45000m, (decimal)sheet.Cell(2, 13).GetDouble());
        Assert.Equal("Belum Lunas", sheet.Cell(2, 11).GetString());
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void Menu_data_penjualan_follows_sale_create_or_sale_history() => OnSta(() =>
    {
        void Check(string[] permissions, bool visible)
        {
            var session = new SessionState();
            session.SetLogin(Samples.LoginWith(permissions));
            var vm = new MainViewModel(session, new FormFactories(
                StockTotal: () => new StockTotalViewModel(_api, _dialogs, new MemoryPreferenceStore<StockTotalViewSettings>(new()), (_, _) => Task.CompletedTask),
                SalesList: Vm));
            var window = ShowOffScreen(new MainWindow { DataContext = vm });
            try
            {
                Assert.Equal(visible, Find<MenuItem>(window, "SalesMenu").IsVisible);
                if (!visible)
                    return;
                Find<MenuItem>(window, "SalesMenu").IsSubmenuOpen = true;
                DoEvents();
                Assert.Equal(Visibility.Visible, Find<MenuItem>(window, "SalesListMenuItem").Visibility);
                Find<MenuItem>(window, "SalesListMenuItem").Command.Execute(null);
                Find<MenuItem>(window, "SalesListMenuItem").Command.Execute(null);
                Assert.Single(vm.Tabs.OfType<SalesListViewModel>());
                Assert.IsType<SalesListViewModel>(vm.SelectedTab);
            }
            finally
            {
                window.Close();
            }
        }

        Check(["sale.history", "stock.view"], visible: true);
        Check(["sale.create"], visible: true);
        Check(["stock.view"], visible: false);
    });
}
