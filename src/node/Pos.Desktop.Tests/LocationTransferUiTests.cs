using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Form Mutasi Lokasi and Daftar Mutasi Lokasi (9c) through real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class LocationTransferUiTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();

    public LocationTransferUiTests() => _api.Catalog.AddRange([Samples.Amplop, Samples.AmplopPak, Samples.Kantong]);

    private LocationTransferViewModel Form() => new(_api, _dialogs, (_, _) => Task.CompletedTask);

    private void WithForm(Action<LocationTransferView, LocationTransferViewModel> test) => OnSta(() =>
    {
        var vm = Form();
        var view = new LocationTransferView { DataContext = vm };
        var window = Host(view);
        try
        {
            DoEvents();
            test(view, vm);
        }
        finally
        {
            window.Close();
        }
    });

    private static UIElement Focused(DependencyObject scope) =>
        (UIElement)FocusManager.GetFocusedElement(FocusManager.GetFocusScope(scope))!;

    private static void Scan(LocationTransferView view, string code, string qty = "1")
    {
        Find<TextBox>(view, "QtyBox").Text = qty;
        var search = Find<TextBox>(view, "SearchBox");
        search.Text = code;
        PressKey(search, Key.Enter);
        DoEvents();
    }

    private static List<string> Cells(DataGrid grid, string header)
    {
        grid.UpdateLayout();
        var column = grid.Columns.Single(c => (string)c.Header == header);
        return grid.Items.Cast<object>().Select(i => column.GetCellContent(i) is TextBlock t ? t.Text : "").ToList();
    }

    [Fact]
    public void Starts_from_the_storage_to_the_display_location() => WithForm((view, vm) =>
    {
        Assert.Equal("STORAGE - Gudang toko", ((LocationOption)Find<ComboBox>(view, "FromLocationBox").SelectedItem).Display);
        Assert.Equal("DISPLAY - Display", ((LocationOption)Find<ComboBox>(view, "ToLocationBox").SelectedItem).Display);
        Assert.True(HasFocus(Find<TextBox>(view, "SearchBox")));
    });

    [Fact]
    public void Scanning_with_x_adds_a_line_with_source_stock_and_the_fefo_preview() => WithForm((view, vm) =>
    {
        Scan(view, "13589", qty: "3");

        var grid = Find<DataGrid>(view, "LinesGrid");
        Assert.Equal(["Amplop"], Cells(grid, "Nama Barang").Select(n => n.Contains("amplop", StringComparison.OrdinalIgnoreCase) ? "Amplop" : n));
        Assert.Equal(["3"], Cells(grid, "Qty"));
        Assert.Equal(["30"], Cells(grid, "Stok Asal"));
        Assert.Equal(["02/12/2026: 3"], Cells(grid, "Alokasi Expired (FEFO)"));
        Assert.Equal([""], Cells(grid, "Keterangan"));
        Assert.Contains($"transfer-preview:{Stock.Storage.Id}:1", _api.Calls);
        Assert.Equal("1", Find<TextBox>(view, "QtyBox").Text);
        Assert.True(HasFocus(Find<TextBox>(view, "SearchBox")));
    });

    [Fact]
    public void Shortage_is_warned_but_saving_is_still_allowed() => WithForm((view, vm) =>
    {
        _api.SourceStock[Samples.AmplopId] = 2m;

        Scan(view, "13589", qty: "5");

        var grid = Find<DataGrid>(view, "LinesGrid");
        Assert.Equal(["Stok asal kurang 3 PCS"], Cells(grid, "Keterangan"));
        Assert.Equal(["02/12/2026: 2  ·  Tanpa exp: 3"], Cells(grid, "Alokasi Expired (FEFO)"));
        Assert.Contains("tidak cukup untuk 1 barang", Find<TextBlock>(view, "WarningText").Text);
        Assert.Equal(Visibility.Visible, Find<TextBlock>(view, "WarningText").Visibility);

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F6);
        DoEvents();

        Assert.Single(_api.TransferRequests);
    });

    [Fact]
    public void Search_results_use_the_standard_keys() => WithForm((view, vm) =>
    {
        var search = Find<TextBox>(view, "SearchBox");
        foreach (var c in "amplop")
            TypeChar(search, c.ToString());
        DoEvents();
        Find<DataGrid>(view, "ProductGrid").UpdateLayout();

        PressKey(search, Key.Down);
        PressKey(Focused(view), Key.Down);                                         // the PAK row
        PressKey(Focused(view), Key.Enter);
        DoEvents();

        Assert.Equal(["PAK"], vm.Lines.Select(l => l.UnitCode));
        Assert.True(HasFocus(search));
    });

    [Fact]
    public void F8_shift_delete_and_the_unit_button_edit_lines() => WithForm((view, vm) =>
    {
        Scan(view, "13589", qty: "3");
        Scan(view, "5113", qty: "2");
        var grid = Find<DataGrid>(view, "LinesGrid");
        grid.SelectedIndex = 0;

        _dialogs.Quantity = 7m;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.F8);
        DoEvents();
        Assert.Equal(["7", "2"], Cells(grid, "Qty"));

        _api.OnUnits = _ => [Samples.Amplop, Samples.AmplopPak];
        _dialogs.UnitToPick = units => units.Single(u => u.UnitCode == "PAK");
        grid.UpdateLayout();
        var unitButton = VisualTreeSearch.Descendants<Button>(grid).First(b => (string)b.Content == "▾");
        Click(unitButton);
        DoEvents();
        Assert.Equal("PAK", vm.Lines[0].UnitCode);
        Assert.Equal("3 PAK", Cells(grid, "Stok Asal")[0]);                       // 30 PCS at the source shown in PAK of 10

        grid.SelectedIndex = 1;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.Delete);                    // plain Delete does nothing
        Assert.Equal(2, vm.Lines.Count);
        var shiftDelete = view.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.Delete && b.Modifiers == ModifierKeys.Shift);
        shiftDelete.Command.Execute(null);
        DoEvents();
        Assert.Equal(["13589"], vm.Lines.Select(l => l.Code));
        Assert.Equal(1, vm.Lines[0].No);
    });

    [Fact]
    public void F6_saves_lines_locations_and_notes_then_clears_the_form() => WithForm((view, vm) =>
    {
        Scan(view, "13589", qty: "3");
        Scan(view, "5113", qty: "2");
        Find<TextBox>(view, "NotesBox").Text = "isi etalase";

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F6);
        DoEvents();

        var request = Assert.Single(_api.TransferRequests);
        Assert.Equal(7, request.Id.Version);
        Assert.Equal((Stock.Storage.Id, Stock.Display.Id, "isi etalase"), (request.FromLocationId, request.ToLocationId, request.Notes));
        Assert.Equal([(Samples.Amplop.ProductUnitId, 3m), (Samples.Kantong.ProductUnitId, 2m)], request.Lines.Select(l => (l.ProductUnitId, l.Qty)));
        Assert.Empty(Find<DataGrid>(view, "LinesGrid").Items);
        Assert.StartsWith("Mutasi TK01-MT-202611-000001 tersimpan", Find<TextBlock>(view, "StatusText").Text);
        Assert.True(HasFocus(Find<TextBox>(view, "SearchBox")));
    });

    [Fact]
    public void Enter_on_the_save_button_saves() => WithForm((view, vm) =>
    {
        Scan(view, "13589");
        var save = Find<Button>(view, "SaveButton");
        save.Focus();

        PressKey(save, Key.Enter);
        DoEvents();

        Assert.Single(_api.TransferRequests);
    });

    [Fact]
    public void Same_source_and_destination_is_refused_before_sending() => WithForm((view, vm) =>
    {
        Scan(view, "13589");
        Find<ComboBox>(view, "ToLocationBox").SelectedItem = vm.FromLocation;

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F6);

        Assert.Empty(_api.TransferRequests);
        Assert.Equal("Lokasi asal dan tujuan tidak boleh sama.", Find<TextBlock>(view, "StatusText").Text);
    });

    [Fact]
    public void A_retry_after_a_network_failure_sends_the_same_id_and_a_change_gives_a_new_one() => WithForm((view, vm) =>
    {
        Scan(view, "13589", qty: "3");
        _api.NextTransferFailure = new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null);

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F6);
        DoEvents();
        Assert.Contains("mutasi yang sama", Find<TextBlock>(view, "StatusText").Text);
        Assert.Single(vm.Lines);

        PressKey(Find<TextBox>(view, "SearchBox"), Key.F6);
        DoEvents();

        Assert.Equal(2, _api.TransferRequests.Count);
        Assert.Equal(_api.TransferRequests[0].Id, _api.TransferRequests[1].Id);
        Assert.Empty(vm.Lines);

        Scan(view, "13589");
        Assert.Null(vm.PendingId);                                                 // a new document gets a new id
    });

    [Fact]
    public void Escape_with_lines_asks_before_closing() => WithForm((view, vm) =>
    {
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        Scan(view, "13589");

        _dialogs.ConfirmAnswer = false;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.Escape);
        Assert.False(closed);
        Assert.Contains(_dialogs.Asked, a => a.StartsWith("confirm:Mutasi belum disimpan", StringComparison.Ordinal));

        _dialogs.ConfirmAnswer = true;
        PressKey(Find<TextBox>(view, "SearchBox"), Key.Escape);
        Assert.True(closed);
    });

    // ---- Daftar Mutasi Lokasi and Kartu Stok -------------------------------------------------------

    [Fact]
    public void Daftar_mutasi_lists_the_period_and_double_click_shows_the_detail() => OnSta(() =>
    {
        var id = Guid.CreateVersion7();
        _api.TransferList.Add(new LocationTransferSummary(id, "TK01-MT-202611-000001", new DateTime(2026, 11, 22, 3, 30, 0, DateTimeKind.Utc),
            "STORAGE", "DISPLAY", "Admin Toko Dev", 1, "isi etalase", "Asia/Jakarta"));
        var vm = new LocationTransferListViewModel(_api, _dialogs, () => new DateOnly(2026, 11, 22));
        var view = new LocationTransferListView { DataContext = vm };
        var window = Host(view);
        try
        {
            DoEvents();
            var grid = Find<DataGrid>(view, "RowsGrid");
            Assert.Contains("transfers:2026-11-01:2026-11-22", _api.Calls);
            Assert.Equal(["22/11/2026 10:30"], Cells(grid, "Tanggal"));            // WIB
            Assert.Equal(["TK01-MT-202611-000001"], Cells(grid, "No Mutasi"));
            Assert.Equal(["STORAGE"], Cells(grid, "Dari"));
            Assert.Equal(["DISPLAY"], Cells(grid, "Ke"));

            var doubleClick = grid.InputBindings.OfType<MouseBinding>().Single();
            Assert.Same(vm.OpenDetailCommand, doubleClick.Command);
            doubleClick.Command.Execute(doubleClick.CommandParameter);
            DoEvents();

            Assert.Contains($"transfer:{id}", _api.Calls);
            var (title, _, lines) = Assert.Single(_dialogs.Documents);
            Assert.Equal("Mutasi Lokasi TK01-MT-202611-000001", title);
            Assert.Contains("Dari      : STORAGE", lines);
            Assert.Contains("   2 PAK = 20 PCS", lines);
            Assert.Contains(lines, l => l.Contains("02/12/2026") && l.Contains("12 PCS"));
            Assert.Contains(lines, l => l.Contains("Tanpa expired") && l.Contains("8 PCS"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void A_transfer_row_on_the_kartu_stok_opens_the_transfer_detail() => OnSta(() =>
    {
        var transferId = Guid.CreateVersion7();
        var card = Stock.Card(1);
        _api.StockCardData = card with
        {
            Rows = [card.Rows[0] with { DocType = "LOCATION_TRANSFER", DocLabel = "Mutasi", DocId = transferId, DocNumber = "TK01-MT-202611-000001" }],
        };
        var vm = new StockCardViewModel(_api, _dialogs, () => new DateOnly(2026, 11, 22));
        var view = new StockCardView { DataContext = vm };
        var window = Host(view);
        try
        {
            vm.ShowAsync(Samples.AmplopId, null).GetAwaiter().GetResult();
            DoEvents();
            var grid = Find<DataGrid>(view, "RowsGrid");
            grid.SelectedIndex = 1;

            PressKey(grid, Key.Enter);
            DoEvents();

            Assert.Contains($"transfer:{transferId}", _api.Calls);
            Assert.Equal("Mutasi Lokasi TK01-MT-202611-000001", Assert.Single(_dialogs.Documents).Title);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- menu --------------------------------------------------------------------------------------

    private MainViewModel Main(params string[] permissions)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith(permissions));
        return new MainViewModel(session, new FormFactories(
            StockTotal: () => new StockTotalViewModel(_api, _dialogs, new MemoryPreferenceStore<StockTotalViewSettings>(new()), (_, _) => Task.CompletedTask),
            StockCheck: () => new StockCheckViewModel(_api, _dialogs),
            LocationTransfer: Form,
            LocationTransferList: () => new LocationTransferListViewModel(_api, _dialogs, () => new DateOnly(2026, 11, 22))));
    }

    [Fact]
    public void Menu_items_follow_stock_view_and_stock_transfer() => OnSta(() =>
    {
        void Check(string[] permissions, bool stockMenu, bool stockTotal, bool transfer, bool list, Type? firstTab)
        {
            var vm = Main(permissions);
            var window = ShowOffScreen(new MainWindow { DataContext = vm });
            try
            {
                Find<MenuItem>(window, "StockMenu").IsSubmenuOpen = true;
                DoEvents();
                Assert.Equal(stockMenu, Find<MenuItem>(window, "StockMenu").IsVisible);
                if (stockMenu)
                {
                    Assert.Equal(stockTotal, Find<MenuItem>(window, "StockTotalMenuItem").Visibility == Visibility.Visible);
                    Assert.Equal(transfer, Find<MenuItem>(window, "LocationTransferMenuItem").Visibility == Visibility.Visible);
                    Assert.Equal(list, Find<MenuItem>(window, "LocationTransferListMenuItem").Visibility == Visibility.Visible);
                }
                Assert.Equal(firstTab, vm.Tabs.FirstOrDefault()?.GetType());
            }
            finally
            {
                window.Close();
            }
        }

        Check(["stock.view", "stock.transfer"], stockMenu: true, stockTotal: true, transfer: true, list: true, typeof(StockTotalViewModel));
        Check(["stock.transfer"], stockMenu: true, stockTotal: false, transfer: true, list: true, typeof(LocationTransferViewModel));
        Check(["stock.view"], stockMenu: true, stockTotal: true, transfer: false, list: true, typeof(StockTotalViewModel));
        Check(["shift.open"], stockMenu: false, stockTotal: false, transfer: false, list: false, null);
    });

    [Fact]
    public void Menu_opens_mutasi_and_daftar_mutasi_as_tabs() => OnSta(() =>
    {
        var vm = Main("stock.view", "stock.transfer");
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            Find<MenuItem>(window, "LocationTransferMenuItem").Command.Execute(null);
            Find<MenuItem>(window, "LocationTransferListMenuItem").Command.Execute(null);
            Find<MenuItem>(window, "LocationTransferMenuItem").Command.Execute(null);
            DoEvents();

            Assert.Equal(["Form Stok Total", "Form Mutasi Lokasi", "Daftar Mutasi Lokasi"], vm.Tabs.Select(t => t.Title));
            Assert.IsType<LocationTransferViewModel>(vm.SelectedTab);
            var tabs = Find<TabControl>(window, "FormTabs");
            tabs.UpdateLayout();
            DoEvents();
            Assert.Single(VisualTreeSearch.Descendants<LocationTransferView>(tabs));
        }
        finally
        {
            window.Close();
        }
    });
}
