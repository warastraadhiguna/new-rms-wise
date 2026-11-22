using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using Pos.Desktop.Views.Dialogs;
using Pos.Shared.Sales;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Retur penjualan (10c) through real controls: form, Data Retur, Tukar Barang in Form Bayar, menus.</summary>
[Collection(WpfUiCollection.Name)]
public class ReturnUiTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new() { ConfirmAnswer = true };
    private readonly FakePrinter _printer = new();

    public ReturnUiTests()
    {
        _api.ReturnSources[Returns.SaleId] = Returns.Source();
        _dialogs.ReasonToChoose = new ReasonChoice(Guid.Parse("01930000-0000-7000-8000-0000000000f1"), "Barang rusak", "kemasan sobek");
    }

    private static SessionState Session(params string[] permissions)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith(permissions.Length == 0 ? ["sale.create", "shift.open"] : permissions));
        return session;
    }

    private ReturnFormViewModel Form(SessionState? session = null) =>
        new(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer), session ?? Session());

    private void WithForm(Action<ReturnFormView, ReturnFormViewModel> test, SessionState? session = null) => OnSta(() =>
    {
        var vm = Form(session);
        var view = new ReturnFormView { DataContext = vm };
        var window = Host(view);
        try
        {
            vm.LoadAsync(Returns.SaleId).GetAwaiter().GetResult();
            DoEvents();
            Find<DataGrid>(view, "LinesGrid").UpdateLayout();
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

    // ---- the form ---------------------------------------------------------------------------------

    [Fact]
    public void Partial_return_with_kondisi_rusak_is_saved_and_printed() => WithForm((view, vm) =>
    {
        var grid = Find<DataGrid>(view, "LinesGrid");
        Assert.Equal("TK01-SL-202611-000123", Find<TextBlock>(view, "NotaText").Text);
        Assert.Equal("22/11/2026 09:15", Find<TextBlock>(view, "DateText").Text);
        Assert.Equal(["3", "2"], Cells(grid, "Terjual"));
        Assert.Equal(["6.750", "4.500"], Cells(grid, "Harga Retur"));
        Assert.Equal(["UANG KEMBALI", "TUKAR BARANG"], Find<ComboBox>(view, "SettlementBox").Items.Cast<SettlementOption>().Select(o => o.Label));

        _dialogs.Quantity = 2m;
        PressKey(grid, Key.F8);                                                     // amplop: 2
        grid.SelectedIndex = 1;
        _dialogs.Quantity = 1m;
        PressKey(grid, Key.Enter);                                                  // Enter = Atur Jumlah too
        PressKey(grid, Key.F7);                                                     // kantong: RUSAK
        DoEvents();
        Assert.Equal(["2", "1"], Cells(grid, "Qty Retur"));
        Assert.Equal(["BAIK", "RUSAK"], Cells(grid, "Kondisi"));
        Assert.Equal(["13.500", "4.500"], Cells(grid, "Jumlah"));
        Assert.Equal("18.000", Find<TextBlock>(view, "TotalText").Text);

        PressKey(grid, Key.F6);
        DoEvents();

        var (title, reasons) = Assert.Single(_dialogs.ReasonDialogs);
        Assert.Equal("Retur Nota TK01-SL-202611-000123", title);
        Assert.Equal(["Barang rusak", "Salah barang"], reasons.Select(r => r.Name));
        Assert.Contains(_dialogs.Asked, a => a == "confirm:Simpan retur nota TK01-SL-202611-000123 senilai 18.000 (UANG KEMBALI)?");
        var request = Assert.Single(_api.ReturnRequests);
        Assert.Equal(("REFUND", "kemasan sobek", (string?)null), (request.Settlement, request.Notes, request.ApprovalToken));
        Assert.Equal([(Returns.AmplopLine, 2m, "GOOD"), (Returns.KantongLine, 1m, "DAMAGED")],
            request.Lines.Select(l => (l.SaleLineId, l.Qty, l.Condition)));
        Assert.Equal("Retur TK01-RT-202611-000001", Assert.Single(_printer.Jobs).Document);
        Assert.StartsWith("Retur TK01-RT-202611-000001 tersimpan (Uang Kembali 18.000)", Find<TextBlock>(view, "StatusText").Text);
        Assert.Equal(["2", "1"], Cells(grid, "Sudah Retur"));                         // reloaded from the Node Service
        Assert.Equal(["", ""], Cells(grid, "Qty Retur"));
    });

    [Fact]
    public void More_than_can_be_returned_is_refused_in_the_form() => WithForm((view, vm) =>
    {
        _api.ReturnSources[Returns.SaleId] = Returns.Source(amplopReturned: 2m);
        vm.LoadAsync(Returns.SaleId).GetAwaiter().GetResult();
        DoEvents();
        _dialogs.Quantity = 2m;

        PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F8);
        DoEvents();

        Assert.Equal("Jumlah retur 2023_amplop lebaran karakter maksimal 1 PCS.", Find<TextBlock>(view, "StatusText").Text);
        Assert.Equal(0m, vm.Lines[0].Qty);

        PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F6);                         // nothing to save
        Assert.Equal("Isi jumlah retur dengan Atur Jumlah [F8].", Find<TextBlock>(view, "StatusText").Text);
        Assert.Empty(_api.ReturnRequests);
    });

    [Fact]
    public void After_the_window_a_supervisor_approves()
    {
        _api.ReturnSources[Returns.SaleId] = Returns.Source(withinWindow: false);
        _dialogs.Supervisor = new SupervisorCredentials("spv", "rahasia");
        _api.OnApprove = (action, _, _) => new ApprovalResult("tok-late", action, DateTime.UtcNow.AddMinutes(2), new UserInfo(Guid.CreateVersion7(), "spv", "Supervisor"));
        WithForm((view, vm) =>
        {
            Assert.Equal("Nota lewat batas retur 7 hari: perlu persetujuan supervisor.", Find<TextBlock>(view, "WindowText").Text);
            _dialogs.Quantity = 1m;
            PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F8);
            Click(Find<Button>(view, "SaveButton"));
            DoEvents();

            Assert.Contains("approve:sale.return.late:spv", _api.Calls);
            Assert.Equal("tok-late", Assert.Single(_api.ReturnRequests).ApprovalToken);
        });
    }

    [Fact]
    public void Potong_piutang_is_offered_only_for_a_credit_nota()
    {
        _api.ReturnSources[Returns.SaleId] = Returns.Source(credit: true);
        WithForm((view, vm) =>
        {
            var settlement = Find<ComboBox>(view, "SettlementBox");
            Assert.Equal(["UANG KEMBALI", "TUKAR BARANG", "POTONG PIUTANG"], settlement.Items.Cast<SettlementOption>().Select(o => o.Label));
            settlement.SelectedItem = ReturnFormViewModel.Reduce;
            _dialogs.Quantity = 1m;
            PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F8);
            PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F6);
            DoEvents();

            Assert.Equal("REDUCE_RECEIVABLE", Assert.Single(_api.ReturnRequests).Settlement);
            Assert.Equal("C000001 - Budi Pelanggan", Find<TextBlock>(view, "MemberText").Text);
        });
    }

    [Fact]
    public void A_retry_after_a_network_failure_sends_the_same_return() => WithForm((view, vm) =>
    {
        _dialogs.Quantity = 1m;
        PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F8);
        _api.NextReturnFailure = new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null);

        PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F6);
        DoEvents();
        Assert.Contains("Tekan F6 lagi", Find<TextBlock>(view, "StatusText").Text);
        PressKey(Find<DataGrid>(view, "LinesGrid"), Key.F6);
        DoEvents();

        Assert.Equal(2, _api.ReturnRequests.Count);
        Assert.Equal(_api.ReturnRequests[0], _api.ReturnRequests[1]);
        Assert.Single(_dialogs.ReasonDialogs);
    });

    // ---- from Data Penjualan, Tukar Barang and Data Retur --------------------------------------------

    private MainViewModel Main(SessionState session)
    {
        _api.Catalog.AddRange([Samples.Amplop, Samples.Kantong]);
        return new MainViewModel(session, new FormFactories(
            Sales: () => new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
                Receipts.Service(_api, _dialogs, _printer), (_, _) => Task.CompletedTask),
            SalesList: () => new SalesListViewModel(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer), session, () => Today,
                (_, _) => Task.CompletedTask),
            ReturnForm: () => Form(session),
            ReturnList: () => new ReturnListViewModel(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer), () => Today)),
            _dialogs, new FakeNavigator());
    }

    private static SessionState WithShift(SessionState session)
    {
        session.Shift = Samples.Shift();
        return session;
    }

    [Fact]
    public void F12_in_data_penjualan_opens_the_return_and_tukar_barang_continues_in_form_penjualan() => OnSta(() =>
    {
        _api.SalesRows.Add(SalesRows.Cash(Returns.Source().SaleNumber, Returns.SaleId));
        var vm = Main(WithShift(Session()));
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            Find<MenuItem>(window, "SalesMenu").IsSubmenuOpen = true;
            DoEvents();
            Find<MenuItem>(window, "SalesListMenuItem").Command.Execute(null);
            var list = Assert.IsType<SalesListViewModel>(vm.SelectedTab);
            list.Initialization.GetAwaiter().GetResult();
            var tabs = Find<TabControl>(window, "FormTabs");
            tabs.UpdateLayout();
            DoEvents();
            var listView = Assert.Single(VisualTreeSearch.Descendants<SalesListView>(tabs));
            Assert.Equal("Retur [F12]", Find<Button>(listView, "ReturnButton").Content);

            PressKey(Find<DataGrid>(listView, "RowsGrid"), Key.F12);
            DoEvents();
            var form = Assert.IsType<ReturnFormViewModel>(vm.SelectedTab);
            for (var i = 0; i < 20 && form.Source is null; i++)
                DoEvents();
            Assert.Equal(Returns.SaleId, form.Source?.SaleId);

            form.SelectedSettlement = ReturnFormViewModel.Exchange;
            _dialogs.Quantity = 2m;
            form.ChangeQtyCommand.Execute(null);
            form.SaveCommand.Execute(null);
            DoEvents();

            var sales = Assert.IsType<SalesViewModel>(vm.SelectedTab);
            Assert.Equal("TK01-RT-202611-000001", sales.ExchangeReturn?.Number);
            tabs.UpdateLayout();
            DoEvents();
            var salesView = Assert.Single(VisualTreeSearch.Descendants<SalesView>(tabs));
            Assert.Equal("Tukar Barang: Retur TK01-RT-202611-000001 senilai 13.500 dipakai saat Bayar [F6].",
                Find<TextBlock>(salesView, "ExchangeText").Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Tukar_barang_without_an_open_shift_on_this_pc_tells_where_to_continue() => OnSta(() =>
    {
        var session = Session("sale.create", "stock.view");
        session.ForeignShift = Samples.Shift(cashierId: Samples.OtherUserId);       // selling locked on this PC
        var vm = Main(session);
        _api.StoredReturns.Clear();
        var form = Form();
        form.LoadAsync(Returns.SaleId).GetAwaiter().GetResult();
        var ret = _api.CreateReturnAsync(new CreateSaleReturnRequest(Guid.CreateVersion7(), Returns.SaleId, "EXCHANGE", Guid.Empty, null,
            [new SaleReturnLineRequest(Returns.AmplopLine, 1m, "GOOD")], null)).GetAwaiter().GetResult();

        vm.StartExchange(ret);

        Assert.StartsWith("Tukar barang untuk retur TK01-RT-202611-000001 dilanjutkan di Form Penjualan", Assert.Single(_dialogs.Messages));
        Assert.Empty(vm.Tabs.OfType<SalesViewModel>());
    });

    [Fact]
    public void Form_bayar_uses_the_return_value_and_sends_the_return_payment() => OnSta(() =>
    {
        var ret = _api.CreateReturnAsync(new CreateSaleReturnRequest(Guid.CreateVersion7(), Returns.SaleId, "EXCHANGE", Guid.Empty, null,
            [new SaleReturnLineRequest(Returns.KantongLine, 2m, "GOOD")], null)).GetAwaiter().GetResult();   // 9.000
        CreateSaleRequest? sent = null;
        _api.OnCreateSale = r => { sent = r; return Samples.Saved(r, "TK01-SL-202611-000124", 15000m, 0m); };
        var cart = new Cart();
        cart.Add(Samples.Amplop.ToCartItem(), 2m);                                      // 15.000
        var vm = new PaymentViewModel(_api, _dialogs, _api.Config, cart, null, Guid.CreateVersion7(), exchange: ret);
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            Assert.Equal("Tukar Barang: Retur TK01-RT-202611-000001 senilai 9.000 — sisa dibayar 6.000", Find<TextBlock>(window, "ExchangeText").Text);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "ExchangeText").Visibility);

            PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);                    // empty = uang pas for the rest
            DoEvents();

            Assert.NotNull(sent);
            Assert.Equal(ret.Id, sent!.ExchangeReturnId);
            Assert.Equal([("RETURN", 9000m, "TK01-RT-202611-000001"), ("CASH", 6000m, (string?)null)],
                sent.Payments.Select(p => (p.Method, p.Amount, p.Reference)));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Form_bayar_refuses_a_nota_smaller_than_the_return_value() => OnSta(() =>
    {
        var ret = _api.CreateReturnAsync(new CreateSaleReturnRequest(Guid.CreateVersion7(), Returns.SaleId, "EXCHANGE", Guid.Empty, null,
            [new SaleReturnLineRequest(Returns.AmplopLine, 3m, "GOOD")], null)).GetAwaiter().GetResult();     // 20.250
        _api.OnCreateSale = r => throw new InvalidOperationException("must not be sent");
        var cart = new Cart();
        cart.Add(Samples.Kantong.ToCartItem(), 1m);                                     // 4.500
        var vm = new PaymentViewModel(_api, _dialogs, _api.Config, cart, null, Guid.CreateVersion7(), exchange: ret);
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);
            DoEvents();

            Assert.StartsWith("Nilai belanja (4.500) kurang dari nilai retur (20.250)", Find<TextBlock>(window, "ErrorText").Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Data_retur_lists_adds_by_nota_number_shows_detail_and_reprints() => OnSta(() =>
    {
        var vm = Main(WithShift(Session()));
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            var exchange = _api.CreateReturnAsync(new CreateSaleReturnRequest(Guid.CreateVersion7(), Returns.SaleId, "EXCHANGE", Guid.Empty, "x",
                [new SaleReturnLineRequest(Returns.AmplopLine, 1m, "GOOD")], null)).GetAwaiter().GetResult();
            Find<MenuItem>(window, "SalesMenu").IsSubmenuOpen = true;
            DoEvents();
            Assert.Equal(Visibility.Visible, Find<MenuItem>(window, "ReturnListMenuItem").Visibility);
            Find<MenuItem>(window, "ReturnListMenuItem").Command.Execute(null);
            var list = Assert.IsType<ReturnListViewModel>(vm.SelectedTab);
            list.Initialization.GetAwaiter().GetResult();
            var tabs = Find<TabControl>(window, "FormTabs");
            tabs.UpdateLayout();
            DoEvents();
            var view = Assert.Single(VisualTreeSearch.Descendants<ReturnListView>(tabs));
            var grid = Find<DataGrid>(view, "RowsGrid");
            Assert.Equal(["TK01-RT-202611-000001"], Cells(grid, "No Retur"));
            Assert.Equal(["Tukar Barang (belum dipakai)"], Cells(grid, "Penyelesaian"));
            Assert.Equal(["22/11/2026 16:45"], Cells(grid, "Tanggal"));                // WIB

            PressKey(grid, Key.F5);                                                     // Cetak Ulang
            DoEvents();
            Assert.Equal("Retur TK01-RT-202611-000001", Assert.Single(_printer.Jobs).Document);

            _dialogs.ChooseDocumentAction = true;
            PressKey(grid, Key.F11);                                                    // detail; unused exchange -> Tukar Barang
            DoEvents();
            Assert.Equal(ReturnListViewModel.ExchangeAction, Assert.Single(_dialogs.DocumentActions));
            Assert.Contains(_dialogs.Documents[0].Lines, l => l.Trim() == "BUKTI RETUR PENJUALAN");
            Assert.Equal(exchange.Id, Assert.IsType<SalesViewModel>(vm.SelectedTab).ExchangeReturn?.Id);

            Find<MenuItem>(window, "ReturnListMenuItem").Command.Execute(null);
            tabs.UpdateLayout();
            DoEvents();
            view = Assert.Single(VisualTreeSearch.Descendants<ReturnListView>(tabs));
            _dialogs.TextToAnswer = "TK01-SL-202611-000123";
            PressKey(view, Key.F1);                                                     // Tambah Retur by nota number
            DoEvents();
            var form = Assert.IsType<ReturnFormViewModel>(vm.SelectedTab);
            for (var i = 0; i < 20 && form.Source is null; i++)
                DoEvents();
            Assert.Contains("return-source:TK01-SL-202611-000123", _api.Calls);
            Assert.Equal(1m, form.Source!.Lines[0].ReturnedQty);
        }
        finally
        {
            window.Close();
        }
    });
}
