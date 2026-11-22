using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using Pos.Desktop.Views.Dialogs;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Tutup Shift (9d) through real controls: blind count, summary, notes, retry, menu, Ctrl+F12, logout.</summary>
[Collection(WpfUiCollection.Name)]
public class CloseShiftUiTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new() { ConfirmAnswer = true };
    private readonly FakePrinter _printer = new();
    private readonly ShiftInfo _shift = Samples.Shift() with { CashierName = "Kasir Satu" };

    private CloseShiftViewModel Vm(Guid? userId = null, string? printerName = "EPSON TM-T82II Receipt") =>
        new(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer, printerName), _shift, userId ?? Samples.UserId);

    private void WithDialog(CloseShiftViewModel vm, Action<CloseShiftDialog> test) => OnSta(() =>
    {
        vm.Initialization.GetAwaiter().GetResult();
        var window = ShowOffScreen(new CloseShiftDialog { DataContext = vm });
        try
        {
            DoEvents();
            test(window);
        }
        finally
        {
            if (window.IsLoaded)
                window.Close();
        }
    });

    private static void Type(UIElement box, string text)
    {
        foreach (var c in text)
            TypeChar(box, c.ToString());
        DoEvents();
    }

    private static bool Visible(FrameworkElement element) => element.IsVisible;

    // ---- the dialog -------------------------------------------------------------------------------

    [Fact]
    public void Blind_count_asks_the_counted_cash_first_then_shows_the_summary_and_the_difference() => WithDialog(Vm(), window =>
    {
        Assert.Equal("TK01-SH-202611-000007", Find<TextBlock>(window, "ShiftNumberText").Text);
        Assert.Equal("Kasir Satu", Find<TextBlock>(window, "CashierText").Text);
        Assert.Equal("22/11/2026 07:00", Find<TextBlock>(window, "OpenedText").Text);   // WIB
        Assert.True(Visible(Find<TextBlock>(window, "BlindHint")));
        Assert.False(Visible(Find<Border>(window, "SummaryPanel")));                     // no system figures yet
        var counted = Find<TextBox>(window, "CountedBox");
        Assert.True(HasFocus(counted));

        Type(counted, "267500");
        Assert.Equal("267.500", counted.Text);
        PressKey(counted, Key.Enter);
        DoEvents();

        var request = Assert.Single(_api.CloseRequests);
        Assert.Equal((_shift.Id, 267500m, (string?)null), (request.ShiftId, request.CountedCash, request.Notes));
        Assert.Contains(_dialogs.Asked, a => a.StartsWith("confirm:Tutup shift TK01-SH-202611-000007 dengan Kas Fisik 267.500", StringComparison.Ordinal));
        Assert.True(Visible(Find<Border>(window, "SummaryPanel")));
        Assert.False(Visible(Find<TextBlock>(window, "BlindHint")));
        Assert.Equal("200.000", Find<TextBlock>(window, "OpeningValue").Text);
        Assert.Equal("40.000", Find<TextBlock>(window, "CashSalesValue").Text);
        Assert.Equal("15.000", Find<TextBlock>(window, "DownPaymentValue").Text);
        Assert.Equal("25.000", Find<TextBlock>(window, "ReceivableValue").Text);
        Assert.Equal("-12.000", Find<TextBlock>(window, "ExpensesValue").Text);
        Assert.Equal("268.000", Find<TextBlock>(window, "ExpectedValue").Text);
        Assert.Equal("267.500", Find<TextBlock>(window, "CountedValue").Text);
        Assert.Equal("Selisih (Kurang)", Find<TextBlock>(window, "DifferenceLabel").Text);
        Assert.Equal("-500", Find<TextBlock>(window, "DifferenceValue").Text);
        Assert.Equal(Colors.Firebrick, ((SolidColorBrush)Find<TextBlock>(window, "DifferenceValue").Foreground).Color);
        Assert.Equal("7", Find<TextBlock>(window, "SaleCountValue").Text);
        Assert.Equal("2", Find<TextBlock>(window, "CancellationCountValue").Text);
        Assert.Equal(4, Find<ItemsControl>(window, "NonCashList").Items.Count);
        Assert.False(counted.IsEnabled);                                                  // closed: nothing can be changed
        Assert.False(Visible(Find<Button>(window, "SaveButton")));

        var job = Assert.Single(_printer.Jobs);                                           // printed at once, no drawer pulse
        Assert.Equal("Tutup Shift TK01-SH-202611-000007", job.Document);
        Assert.DoesNotContain((byte)0x70, job.Data.Take(10));
        Assert.Equal("Shift ditutup. Laporan tutup shift dicetak.", Find<TextBlock>(window, "StatusText").Text);
    });

    [Fact]
    public void Selesai_with_enter_ends_the_dialog_after_closing() => WithDialog(Vm(), window =>
    {
        var vm = (CloseShiftViewModel)window.DataContext;
        Find<TextBox>(window, "CountedBox").Text = "268.000";
        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        DoEvents();
        Assert.Equal("Selisih (Pas)", Find<TextBlock>(window, "DifferenceLabel").Text);
        Assert.True(Visible(Find<Button>(window, "FinishButton")));

        PressKey(window, Key.F5);                                                         // Cetak Ulang
        Assert.Equal(2, _printer.Jobs.Count);
        PressKey(window, Key.Enter);
        DoEvents();

        Assert.True(vm.DialogResult);
        Assert.False(window.IsLoaded);
    });

    [Fact]
    public void Over_is_green() => WithDialog(Vm(), window =>
    {
        Find<TextBox>(window, "CountedBox").Text = "270.000";
        Click(Find<Button>(window, "SaveButton"));
        DoEvents();

        Assert.Equal("Selisih (Lebih)", Find<TextBlock>(window, "DifferenceLabel").Text);
        Assert.Equal("2.000", Find<TextBlock>(window, "DifferenceValue").Text);
        Assert.Equal(Color.FromRgb(0x1E, 0x6B, 0x2E), ((SolidColorBrush)Find<TextBlock>(window, "DifferenceValue").Foreground).Color);
    });

    [Fact]
    public void Without_blind_count_the_running_summary_is_shown_before_counting()
    {
        _api.OnShiftClosing = id => Shifts.Open(id);
        WithDialog(Vm(), window =>
        {
            Assert.True(Visible(Find<Border>(window, "SummaryPanel")));
            Assert.False(Visible(Find<TextBlock>(window, "BlindHint")));
            Assert.Equal("268.000", Find<TextBlock>(window, "ExpectedValue").Text);
            Assert.False(Visible(Find<Grid>(window, "ResultPanel")));                     // no counted cash yet
        });
    }

    [Fact]
    public void Empty_counted_cash_or_a_declined_confirmation_sends_nothing() => WithDialog(Vm(), window =>
    {
        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        Assert.StartsWith("Isi Kas Fisik", Find<TextBlock>(window, "StatusText").Text);

        _dialogs.ConfirmAnswer = false;
        Find<TextBox>(window, "CountedBox").Text = "100.000";
        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);

        Assert.Empty(_api.CloseRequests);
        Assert.True(Find<TextBox>(window, "CountedBox").IsEnabled);
    });

    [Fact]
    public void Closing_another_cashiers_shift_needs_keterangan() => WithDialog(Vm(userId: Samples.OtherUserId), window =>
    {
        Assert.Equal("Keterangan (wajib)", Find<TextBlock>(window, "NotesLabel").Text);
        Find<TextBox>(window, "CountedBox").Text = "268.000";
        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        DoEvents();

        Assert.Empty(_api.CloseRequests);
        Assert.Equal("Isi Keterangan: shift ini milik Kasir Satu.", Find<TextBlock>(window, "StatusText").Text);
        Assert.True(HasFocus(Find<TextBox>(window, "NotesBox")));

        Type(Find<TextBox>(window, "NotesBox"), "kasir pulang sakit");
        PressKey(Find<TextBox>(window, "NotesBox"), Key.Enter);
        DoEvents();

        Assert.Equal("kasir pulang sakit", Assert.Single(_api.CloseRequests).Notes);
    });

    [Fact]
    public void A_retry_after_a_network_failure_sends_the_same_request() => WithDialog(Vm(), window =>
    {
        _api.NextCloseFailure = new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null);
        Find<TextBox>(window, "CountedBox").Text = "268.000";

        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        DoEvents();
        Assert.Contains("Tekan Simpan lagi", Find<TextBlock>(window, "StatusText").Text);
        Assert.True(Find<TextBox>(window, "CountedBox").IsEnabled);

        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        DoEvents();

        Assert.Equal(2, _api.CloseRequests.Count);
        Assert.Equal(_api.CloseRequests[0], _api.CloseRequests[1]);
        Assert.True(((CloseShiftViewModel)window.DataContext).IsClosed);
    });

    [Fact]
    public void Without_a_printer_the_report_is_shown_on_screen_with_signatures() => WithDialog(Vm(printerName: null), window =>
    {
        Find<TextBox>(window, "CountedBox").Text = "268.000";
        PressKey(Find<TextBox>(window, "CountedBox"), Key.Enter);
        DoEvents();

        Assert.Empty(_printer.Jobs);
        var (message, lines) = Assert.Single(_dialogs.Previews);
        Assert.Contains("Laporan tutup shift ditampilkan di layar", message);
        Assert.Contains(lines, l => l.Trim() == "LAPORAN TUTUP SHIFT");
        Assert.Contains(lines, l => l.Contains("Kasir,") && l.Contains("Supervisor,"));
        Assert.Contains("Dibuka   : 22/11/2026 07:00", lines);
        Assert.Contains("Ditutup  : 22/11/2026 15:05", lines);
    });

    [Fact]
    public void Escape_before_closing_cancels_without_closing() => WithDialog(Vm(), window =>
    {
        var vm = (CloseShiftViewModel)window.DataContext;

        PressKey(Find<TextBox>(window, "CountedBox"), Key.Escape);
        DoEvents();

        Assert.False(vm.DialogResult);
        Assert.False(vm.IsClosed);
        Assert.Empty(_api.CloseRequests);
    });

    // ---- menu, Ctrl+F12 and logout -------------------------------------------------------------------

    private (MainViewModel Vm, SessionState Session, FakeNavigator Navigator) Main(
        string[] permissions, ShiftInfo? shift = null, ShiftInfo? foreignShift = null)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith(permissions));
        session.Shift = shift;
        session.ForeignShift = foreignShift;
        var navigator = new FakeNavigator();
        var vm = new MainViewModel(session, new FormFactories(
            Sales: () => new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
                Receipts.Service(_api, _dialogs), (_, _) => Task.CompletedTask),
            StockTotal: () => new StockTotalViewModel(_api, _dialogs, new MemoryPreferenceStore<StockTotalViewSettings>(new()), (_, _) => Task.CompletedTask),
            CloseShift: s => new CloseShiftViewModel(_api, _dialogs, Receipts.Service(_api, _dialogs, _printer), s, session.User!.Id)),
            _dialogs, navigator);
        return (vm, session, navigator);
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

    private static MenuItem OpenSalesMenu(MainWindow window)
    {
        var menu = Find<MenuItem>(window, "SalesMenu");
        menu.IsSubmenuOpen = true;
        DoEvents();
        return menu;
    }

    [Fact]
    public void Tutup_shift_in_menu_penjualan_closes_and_logs_out()
    {
        var (vm, session, navigator) = Main(["sale.create", "shift.open"], shift: _shift);
        _dialogs.OnCloseShift = closeShift => true;
        WithMain(vm, window =>
        {
            OpenSalesMenu(window);
            var item = Find<MenuItem>(window, "CloseShiftMenuItem");
            Assert.Equal(Visibility.Visible, item.Visibility);
            Assert.Equal("Ctrl+F12", item.InputGestureText);

            item.Command.Execute(null);
            DoEvents();

            var dialog = Assert.Single(_dialogs.CloseShiftDialogs);
            Assert.True(dialog.IsOwner);
            Assert.DoesNotContain(_dialogs.Asked, a => a.StartsWith("confirm:", StringComparison.Ordinal));   // the menu is deliberate
            Assert.False(session.IsLoggedIn);
            Assert.Equal(["login"], navigator.Shown);
        });
    }

    [Fact]
    public void Cancelling_the_dialog_keeps_the_session()
    {
        var (vm, session, navigator) = Main(["sale.create", "shift.open"], shift: _shift);
        WithMain(vm, window =>
        {
            OpenSalesMenu(window);
            Find<MenuItem>(window, "CloseShiftMenuItem").Command.Execute(null);

            Assert.Single(_dialogs.CloseShiftDialogs);
            Assert.True(session.IsLoggedIn);
            Assert.Empty(navigator.Shown);
        });
    }

    private void WithSalesForm(Action<MainViewModel, SalesView, KeyBinding, SessionState, FakeNavigator> test) => OnSta(() =>
    {
        _api.Catalog.Add(Samples.Amplop);
        var (vm, session, navigator) = Main(["sale.create", "shift.open"], shift: _shift);
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            var tabs = Find<TabControl>(window, "FormTabs");
            tabs.UpdateLayout();
            DoEvents();
            var salesView = Assert.Single(VisualTreeSearch.Descendants<SalesView>(tabs));
            var ctrlF12 = salesView.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.F12 && b.Modifiers == ModifierKeys.Control);
            test(vm, salesView, ctrlF12, session, navigator);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Ctrl_F12_in_form_penjualan_asks_first_then_closes_and_logs_out() => WithSalesForm((vm, view, ctrlF12, session, navigator) =>
    {
        _dialogs.OnCloseShift = _ => true;

        _dialogs.ConfirmAnswer = false;
        ctrlF12.Command.Execute(null);
        Assert.Empty(_dialogs.CloseShiftDialogs);
        Assert.Contains("confirm:Tutup shift sekarang? Setelah ditutup Anda akan keluar ke layar login.", _dialogs.Asked);
        Assert.True(session.IsLoggedIn);

        _dialogs.ConfirmAnswer = true;
        ctrlF12.Command.Execute(null);

        Assert.Single(_dialogs.CloseShiftDialogs);
        Assert.False(session.IsLoggedIn);
        Assert.Equal(["login"], navigator.Shown);
    });

    [Fact]
    public void Ctrl_F12_and_the_menu_are_refused_while_a_transaction_is_open() => WithSalesForm((vm, view, ctrlF12, session, navigator) =>
    {
        var search = Find<TextBox>(view, "SearchBox");
        search.Text = "13589";
        PressKey(search, Key.Enter);
        DoEvents();
        var sales = Assert.IsType<SalesViewModel>(vm.SelectedTab);
        Assert.True(sales.HasItems);

        ctrlF12.Command.Execute(null);
        vm.CloseShiftCommand.Execute(null);

        Assert.Empty(_dialogs.CloseShiftDialogs);
        Assert.DoesNotContain(_dialogs.Asked, a => a.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.StartsWith("Selesaikan (Bayar) atau batalkan transaksi dulu", sales.StatusMessage);
        Assert.StartsWith("Masih ada transaksi di Form Penjualan", Assert.Single(_dialogs.Messages));
        Assert.True(session.IsLoggedIn);
    });

    [Fact]
    public void Another_cashiers_shift_can_be_closed_only_with_shift_close() => OnSta(() =>
    {
        void Check(string[] permissions, bool canClose)
        {
            var foreign = _shift with { CashierId = Samples.OtherUserId };
            var (vm, _, _) = Main(permissions, foreignShift: foreign);
            var window = ShowOffScreen(new MainWindow { DataContext = vm });
            try
            {
                Assert.Equal(canClose, vm.CanCloseShift);
                Assert.Equal(canClose || permissions.Contains("sale.create"), Find<MenuItem>(window, "SalesMenu").IsVisible);
                if (canClose)
                {
                    OpenSalesMenu(window);
                    Assert.Equal(Visibility.Visible, Find<MenuItem>(window, "CloseShiftMenuItem").Visibility);
                    Assert.Equal(permissions.Contains("sale.create") ? Visibility.Visible : Visibility.Collapsed,
                        Find<MenuItem>(window, "SalesFormMenuItem").Visibility);
                    Find<MenuItem>(window, "CloseShiftMenuItem").Command.Execute(null);
                    Assert.False(_dialogs.CloseShiftDialogs[^1].IsOwner);                 // Keterangan required
                }
            }
            finally
            {
                window.Close();
            }
        }

        Check(["stock.view", "shift.close"], canClose: true);                              // warehouse/admin without selling
        Check(["sale.create", "stock.view", "shift.close"], canClose: true);
        Check(["sale.create", "stock.view"], canClose: false);
    });
}
