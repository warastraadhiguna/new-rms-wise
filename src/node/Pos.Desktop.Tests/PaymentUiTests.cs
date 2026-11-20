using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using Pos.Desktop.Views.Dialogs;
using Pos.Shared.Sales;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>"Form Bayar Nota" (docs/rms2/form-bayar.png) and F6, driven through real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class PaymentUiTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakePrinter _printer = new();

    public PaymentUiTests() => _api.OnCreateSale = r => Samples.Saved(r, "TK01-SL-202611-000030", 13700m, 6300m);

    private PaymentViewModel Vm(Cart cart, CustomerItem? customer = null) =>
        new(_api, _dialogs, _api.Config, cart, customer, Guid.CreateVersion7());

    private static Cart CartOf(params (ProductItem Item, decimal Qty)[] lines)
    {
        var cart = new Cart();
        foreach (var (item, qty) in lines)
            cart.Add(item.ToCartItem(), qty);
        return cart;
    }

    /// <summary>7.500 + 4.500 + 1.680 = 13.680 (TUNAI grand total 13.700).</summary>
    private static Cart Cart13680() =>
        CartOf((Samples.Amplop, 1m), (Samples.Kantong, 1m), (Samples.Product(Guid.CreateVersion7(), "X1", "Permen", 1680m), 1m));

    private void WithDialog(Cart cart, Action<PaymentDialog, PaymentViewModel> test, CustomerItem? customer = null) => OnSta(() =>
    {
        var vm = Vm(cart, customer);
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            test(window, vm);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Type(UIElement box, string text)
    {
        foreach (var c in text)
            TypeChar(box, c.ToString());
    }

    [Fact]
    public void Layout_matches_rms2_form_bayar_nota() => WithDialog(Cart13680(), (window, _) =>
    {
        Assert.Equal("Form Bayar Nota", window.Title);
        string[] order = ["MethodBox", "TotalBox", "DiscountBox", "GrandTotalBox", "BayarBox", "ChangeBox", "NotesBox", "ReferenceBox"];
        var rows = order.Select(name => Grid.GetRow(Find<FrameworkElement>(window, name))).ToList();
        Assert.Equal(rows.Order().ToList(), rows);
        Assert.Equal(order.Length, rows.Distinct().Count());
        Assert.Equal(Grid.GetRow(Find<ComboBox>(window, "DiscountModeBox")), Grid.GetRow(Find<TextBox>(window, "DiscountBox")));

        Assert.Equal("TUNAI", ((PaymentMethodInfo)Find<ComboBox>(window, "MethodBox").SelectedItem).Name);
        Assert.Equal("Potongan", Find<ComboBox>(window, "DiscountModeBox").SelectedItem);
        Assert.Equal("13.680", Find<TextBox>(window, "TotalBox").Text);
        Assert.Equal("13.700", Find<TextBox>(window, "GrandTotalBox").Text);
        Assert.True(Find<TextBox>(window, "TotalBox").IsReadOnly);
        Assert.True(Find<TextBox>(window, "GrandTotalBox").IsReadOnly);
        Assert.True(Find<TextBox>(window, "ChangeBox").IsReadOnly);
        Assert.Equal(TextAlignment.Right, Find<TextBox>(window, "ChangeBox").TextAlignment);
        Assert.True(Find<TextBox>(window, "GrandTotalBox").FontSize > Find<TextBox>(window, "TotalBox").FontSize);
        Assert.True(Find<TextBox>(window, "BayarBox").FontSize > Find<TextBox>(window, "TotalBox").FontSize);
        Assert.False(Find<TextBox>(window, "ReferenceBox").IsEnabled);           // TUNAI
        Assert.True(HasFocus(Find<TextBox>(window, "BayarBox")));                 // focus starts at Bayar
    });

    [Fact]
    public void Typing_bayar_shows_thousands_and_kembali_and_enter_saves() => WithDialog(Cart13680(), (window, vm) =>
    {
        var bayar = Find<TextBox>(window, "BayarBox");

        Type(bayar, "20000");
        Assert.Equal("20.000", bayar.Text);
        Assert.Equal("6.300", Find<TextBox>(window, "ChangeBox").Text);

        PressKey(bayar, Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(("CASH", 20000m), (request.Payments[0].Method, request.Payments[0].Amount));
        Assert.Equal("TK01-SL-202611-000030", vm.Result?.Sale.Number);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void Enter_in_bayar_with_too_little_money_does_not_save() => WithDialog(Cart13680(), (window, _) =>
    {
        var bayar = Find<TextBox>(window, "BayarBox");
        Type(bayar, "10000");

        PressKey(bayar, Key.Enter);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Jumlah bayar kurang dari total.", Find<TextBlock>(window, "ErrorText").Text);
        Assert.True(window.IsVisible);
        Assert.True(HasFocus(bayar));
    });

    private static readonly CustomerItem CreditCustomer =
        new(Guid.CreateVersion7(), "C000001", "Budi Pelanggan", null, 500000m, 0m, 500000m, 14, 0);

    [Theory]
    [InlineData("CASH", 13700)]          // cash: the rounded Grand Total
    [InlineData("DEBIT", 13680)]
    [InlineData("QRIS", 13680)]
    [InlineData("TRANSFER", 13680)]
    public void Enter_on_an_empty_bayar_pays_the_exact_amount_and_saves(string method, int expected) =>
        WithDialog(Cart13680(), (window, vm) =>
        {
            Find<ComboBox>(window, "MethodBox").SelectedItem = _api.Config.PaymentMethods.Single(m => m.Code == method);
            DoEvents();
            var bayar = Find<TextBox>(window, "BayarBox");
            bayar.Focus();
            bayar.Clear();                                   // non-cash fills Bayar; the cashier may have emptied it
            Assert.Equal("", bayar.Text);

            PressKey(bayar, Key.Enter);

            var request = Assert.Single(_api.SaleRequests);
            Assert.Equal((method, (decimal)expected), (request.Payments[0].Method, request.Payments[0].Amount));
            Assert.Equal(expected.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("id-ID")), bayar.Text);
            Assert.Equal("0", Find<TextBox>(window, "ChangeBox").Text);
            Assert.True(vm.DialogResult);
            Assert.False(window.IsVisible);
        });

    [Fact]
    public void Enter_on_an_empty_bayar_right_after_opening_saves_cash_uang_pas() => WithDialog(Cart13680(), (window, _) =>
    {
        var bayar = Find<TextBox>(window, "BayarBox");
        Assert.True(HasFocus(bayar));

        PressKey(bayar, Key.Enter);

        Assert.Equal(13700m, Assert.Single(_api.SaleRequests).Payments[0].Amount);
    });

    [Fact]
    public void Enter_on_an_empty_bayar_after_a_potongan_pays_the_new_grand_total() => WithDialog(Cart13680(), (window, _) =>
    {
        var discount = Find<TextBox>(window, "DiscountBox");
        discount.Focus();
        discount.SelectAll();
        Type(discount, "1000");
        PressKey(discount, Key.Enter);                       // moves to Bayar

        PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(12700m, request.Payments[0].Amount);
        Assert.Equal(1000m, request.DiscountAmount);
    });

    [Fact]
    public void Enter_on_an_empty_keterangan_with_an_empty_bayar_still_says_too_little() => WithDialog(Cart13680(), (window, _) =>
    {
        var notes = Find<TextBox>(window, "NotesBox");
        notes.Focus();

        PressKey(notes, Key.Enter);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Jumlah bayar kurang dari total.", Find<TextBlock>(window, "ErrorText").Text);
    });

    // ---- KREDIT: Uang Muka (DP) and Sisa Piutang --------------------------------------------------

    private void SelectCredit(PaymentDialog window)
    {
        Find<ComboBox>(window, "MethodBox").SelectedItem = _api.Config.PaymentMethods.Single(m => m.IsCredit);
        DoEvents();
    }

    [Fact]
    public void Kredit_renames_bayar_to_uang_muka_and_kembali_to_sisa_piutang() => WithDialog(Cart13680(), (window, _) =>
    {
        Assert.Equal("Bayar", Find<TextBlock>(window, "BayarLabel").Text);
        Assert.Equal("Kembali", Find<TextBlock>(window, "ChangeLabel").Text);

        SelectCredit(window);

        Assert.Equal("Uang Muka (DP)", Find<TextBlock>(window, "BayarLabel").Text);
        Assert.Equal("Sisa Piutang", Find<TextBlock>(window, "ChangeLabel").Text);
        Assert.Equal("", Find<TextBox>(window, "BayarBox").Text);                 // DP starts empty
        Assert.Equal("13.680", Find<TextBox>(window, "GrandTotalBox").Text);      // no cash, no rounding
        Assert.Equal("13.680", Find<TextBox>(window, "ChangeBox").Text);          // all of it is receivable

        Find<ComboBox>(window, "MethodBox").SelectedItem = _api.Config.PaymentMethods.Single(m => m.IsCash);
        DoEvents();
        Assert.Equal("Bayar", Find<TextBlock>(window, "BayarLabel").Text);
        Assert.Equal("Kembali", Find<TextBlock>(window, "ChangeLabel").Text);
    }, customer: CreditCustomer);

    [Fact]
    public void Kredit_enter_on_an_empty_dp_saves_dp_0_and_the_whole_grand_total_as_credit() => WithDialog(Cart13680(), (window, vm) =>
    {
        SelectCredit(window);
        var dp = Find<TextBox>(window, "BayarBox");
        dp.Focus();

        PressKey(dp, Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(CreditCustomer.Id, request.CustomerId);
        var payment = Assert.Single(request.Payments);
        Assert.Equal(("CREDIT", 13680m), (payment.Method, payment.Amount));
        Assert.True(vm.DialogResult);
    }, customer: CreditCustomer);

    [Fact]
    public void Kredit_with_a_dp_sends_the_dp_as_tunai_and_the_rest_as_credit() => WithDialog(Cart13680(), (window, _) =>
    {
        SelectCredit(window);
        var dp = Find<TextBox>(window, "BayarBox");
        dp.Focus();
        Type(dp, "5000");

        Assert.Equal("5.000", dp.Text);
        Assert.Equal("13.680", Find<TextBox>(window, "GrandTotalBox").Text);      // KREDIT is never rounded
        Assert.Equal("8.680", Find<TextBox>(window, "ChangeBox").Text);           // Sisa Piutang

        PressKey(dp, Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal([("CASH", 5000m), ("CREDIT", 8680m)], request.Payments.Select(p => (p.Method, p.Amount)));
    }, customer: CreditCustomer);

    [Fact]
    public void Kredit_dp_above_the_grand_total_is_refused() => WithDialog(Cart13680(), (window, _) =>
    {
        SelectCredit(window);
        var dp = Find<TextBox>(window, "BayarBox");
        dp.Focus();
        Type(dp, "20000");

        PressKey(dp, Key.Enter);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Uang muka harus kurang dari Grand Total. Kalau dibayar penuh, pilih cara bayar TUNAI.", Find<TextBlock>(window, "ErrorText").Text);
        Assert.True(window.IsVisible);
        Assert.True(HasFocus(dp));
    }, customer: CreditCustomer);

    [Fact]
    public void Kredit_dp_equal_to_the_grand_total_is_refused_use_tunai_instead() => WithDialog(Cart13680(), (window, _) =>
    {
        SelectCredit(window);
        var dp = Find<TextBox>(window, "BayarBox");
        dp.Focus();
        Type(dp, "13680");
        Assert.Equal("0", Find<TextBox>(window, "ChangeBox").Text);

        PressKey(dp, Key.Enter);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Uang muka harus kurang dari Grand Total. Kalau dibayar penuh, pilih cara bayar TUNAI.",
            Find<TextBlock>(window, "ErrorText").Text);
    }, customer: CreditCustomer);

    [Fact]
    public void Kredit_grand_total_never_changes_while_the_dp_is_typed() => WithDialog(Cart13680(), (window, _) =>
    {
        SelectCredit(window);
        var dp = Find<TextBox>(window, "BayarBox");
        var grandTotal = Find<TextBox>(window, "GrandTotalBox");
        dp.Focus();

        foreach (var c in "12345")
        {
            TypeChar(dp, c.ToString());
            Assert.Equal("13.680", grandTotal.Text);
        }
        Assert.Equal("1.335", Find<TextBox>(window, "ChangeBox").Text);       // 13.680 - 12.345
    }, customer: CreditCustomer);

    [Fact]
    public void Choosing_kredit_without_a_member_opens_the_member_search_on_top_of_the_dialog() => OnSta(() =>
    {
        CustomerItem? onForm = null;
        var vm = new PaymentViewModel(_api, _dialogs, _api.Config, Cart13680(), null, Guid.CreateVersion7(), c => onForm = c);
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            _dialogs.CustomerToPick = CreditCustomer;

            SelectCredit(window);

            Assert.Contains("pick-customer:", _dialogs.Asked);
            Assert.True(window.IsVisible);                                          // the dialog stays open
            Assert.Equal("Member: C000001 - Budi Pelanggan", Find<TextBlock>(window, "MemberText").Text);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "MemberText").Visibility);
            Assert.Same(CreditCustomer, onForm);                                    // also set on the sales form
            Assert.True(HasFocus(Find<TextBox>(window, "BayarBox")));               // Uang Muka, just press Enter
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "ErrorText").Visibility);

            PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);

            var request = Assert.Single(_api.SaleRequests);
            Assert.Equal(CreditCustomer.Id, request.CustomerId);
            Assert.Equal(("CREDIT", 13680m), (request.Payments[0].Method, request.Payments[0].Amount));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Cancelled_member_search_keeps_the_dialog_with_a_message_and_f4_opens_it_again() => WithDialog(Cart13680(), (window, vm) =>
    {
        _dialogs.CustomerToPick = null;
        SelectCredit(window);

        Assert.True(window.IsVisible);
        Assert.Equal("Pilih member untuk KREDIT (F4).", Find<TextBlock>(window, "ErrorText").Text);
        Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "MemberText").Visibility);

        // Enter without a member: the search opens again instead of saving
        PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);
        Assert.Empty(_api.SaleRequests);
        Assert.Equal(2, _dialogs.Asked.Count(a => a.StartsWith("pick-customer", StringComparison.Ordinal)));
        Assert.True(window.IsVisible);

        _dialogs.CustomerToPick = CreditCustomer;
        PressKey(Find<TextBox>(window, "BayarBox"), Key.F4);

        Assert.Equal(3, _dialogs.Asked.Count(a => a.StartsWith("pick-customer", StringComparison.Ordinal)));
        Assert.Equal("Member: C000001 - Budi Pelanggan", Find<TextBlock>(window, "MemberText").Text);
        Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "ErrorText").Visibility);
        Assert.Empty(_api.SaleRequests);                                            // chosen, not yet saved

        PressKey(Find<TextBox>(window, "BayarBox"), Key.Enter);
        Assert.Equal(CreditCustomer.Id, Assert.Single(_api.SaleRequests).CustomerId);
    });

    [Fact]
    public void F4_in_the_payment_dialog_works_for_every_method() => WithDialog(Cart13680(), (window, vm) =>
    {
        Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.F4 && ReferenceEquals(b.Command, vm.PickMemberCommand));
        _dialogs.CustomerToPick = CreditCustomer;

        PressKey(Find<TextBox>(window, "BayarBox"), Key.F4);                     // TUNAI

        Assert.Equal("Member: C000001 - Budi Pelanggan", Find<TextBlock>(window, "MemberText").Text);
        Assert.True(HasFocus(Find<TextBox>(window, "BayarBox")));
    });

    [Fact]
    public void Member_chosen_in_the_payment_dialog_appears_on_the_sales_form() => OnSta(() =>
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create"));
        _api.Catalog.Add(Samples.Amplop);
        _dialogs.CustomerToPick = CreditCustomer;
        _dialogs.OnPayment = p =>
        {
            p.SelectedMethod = p.PaymentMethods.Single(m => m.IsCredit);      // opens the member search (fake picks Budi)
            return null;                                                       // cashier closes the dialog
        };
        var vm = new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
            Receipts.Service(_api, _dialogs, _printer), (_, _) => Task.CompletedTask);
        var view = new SalesView { DataContext = vm };
        var window = Host(view);
        try
        {
            var search = Find<TextBox>(view, "SearchBox");
            search.Text = "13589";
            PressKey(search, Key.Enter);

            PressKey(search, Key.F6);
            var firstSaleId = _dialogs.Payments[0].SaleId;

            Assert.Equal("C000001 - Budi Pelanggan", Find<TextBox>(view, "MemberBox").Text);
            Assert.Same(CreditCustomer, vm.Customer);
            Assert.Single(Find<DataGrid>(view, "CartGrid").Items);

            PressKey(search, Key.F6);                                          // same cart → same sale id
            Assert.Equal(firstSaleId, _dialogs.Payments[1].SaleId);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void No_label_is_cut_off_for_any_payment_method() => WithDialog(Cart13680(), (window, vm) =>
    {
        foreach (var method in _api.Config.PaymentMethods)
        {
            _dialogs.CustomerToPick = CreditCustomer;
            Find<ComboBox>(window, "MethodBox").SelectedItem = method;
            DoEvents();
            window.UpdateLayout();

            var labels = VisualTreeSearchLabels(window).ToList();
            Assert.Contains(labels, l => l.Text == (method.IsCredit ? "Uang Muka (DP)" : "Bayar"));
            foreach (var label in labels)
            {
                // Desired width without limits (text + margin) must fit the slot the grid column gives the label.
                // ActualWidth alone cannot show clipping: WPF keeps the full width and clips the drawing.
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var needed = label.DesiredSize.Width;
                label.InvalidateMeasure();
                window.UpdateLayout();
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(label).Width;
                Assert.True(slot >= needed - 0.5, $"{method.Name}: '{label.Text}' needs {needed:0.0} px, slot is {slot:0.0} px");
                Assert.True(label.ActualWidth >= label.DesiredSize.Width - label.Margin.Left - label.Margin.Right - 0.5);
                Assert.Null(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(label));
            }
        }
        var discountMode = Find<ComboBox>(window, "DiscountModeBox");
        Assert.True(discountMode.ActualWidth >= 90, $"Potongan/Diskon box is {discountMode.ActualWidth} px");
    });

    /// <summary>Label text blocks of the form grid (column 0).</summary>
    private static IEnumerable<TextBlock> VisualTreeSearchLabels(Window window) =>
        Pos.Desktop.Infrastructure.VisualTreeSearch.Descendants<TextBlock>(window)
            .Where(t => Grid.GetColumn(t) == 0 && t.Parent is Grid && !string.IsNullOrEmpty(t.Text));

    [Fact]
    public void Credit_limit_warning_is_shown_after_saving_without_blocking() => OnSta(() =>
    {
        var warning = new SaleWarningInfo("credit_limit_exceeded", "Customer credit 13680 exceeds the limit 10000 by 3680.", 10000m, 13680m, 3680m);
        const string expected = "Piutang pelanggan menjadi Rp 13.680, melebihi limit Rp 10.000 (lebih Rp 3.680). Penjualan tetap tersimpan.";
        var result = new SaleResult(new SaleSummary(Guid.CreateVersion7(), "TK01-SL-202611-000050", 13680m, 0m, 0m, 13680m, 0m, 13680m),
            [warning], false);

        var dialog = ShowOffScreen(new SaleResultDialog { DataContext = new SaleResultViewModel(result) });
        Assert.Equal(expected, Find<TextBlock>(dialog, "WarningText").Text);
        Assert.Equal(Visibility.Visible, Find<TextBlock>(dialog, "WarningText").Visibility);
        dialog.Close();

        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create"));
        _api.Catalog.Add(Samples.Amplop);
        _dialogs.OnPayment = _ => result;
        var vm = new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
            Receipts.Service(_api, _dialogs, _printer), (_, _) => Task.CompletedTask);
        var view = new SalesView { DataContext = vm };
        var window = Host(view);
        try
        {
            var search = Find<TextBox>(view, "SearchBox");
            search.Text = "13589";
            PressKey(search, Key.Enter);

            PressKey(search, Key.F6);

            Assert.Empty(Find<DataGrid>(view, "CartGrid").Items);                  // saved, not blocked
            Assert.Equal(expected, Find<TextBlock>(view, "WarningText").Text);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(view, "WarningText").Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Enter_in_potongan_moves_to_bayar_and_updates_grand_total() => WithDialog(Cart13680(), (window, vm) =>
    {
        var discount = Find<TextBox>(window, "DiscountBox");
        discount.Focus();
        discount.SelectAll();
        Type(discount, "1000");
        Assert.Equal("1.000", discount.Text);

        PressKey(discount, Key.Enter);

        Assert.True(HasFocus(Find<TextBox>(window, "BayarBox")));
        Assert.Equal("12.700", Find<TextBox>(window, "GrandTotalBox").Text);   // 12.680 rounded for cash
        Assert.Empty(_api.SaleRequests);
    });

    [Fact]
    public void Diskon_percent_via_the_small_drop_down() => WithDialog(CartOf((Samples.Kantong, 3m)), (window, vm) =>
    {
        Find<ComboBox>(window, "DiscountModeBox").SelectedItem = "Diskon";
        var discount = Find<TextBox>(window, "DiscountBox");
        discount.Focus();
        discount.SelectAll();
        Type(discount, "10");

        Assert.False(vm.IsNominalDiscount);
        Assert.Equal("12.200", Find<TextBox>(window, "GrandTotalBox").Text);   // 13.500 - 10% = 12.150 -> 12.200
    });

    [Fact]
    public void Non_cash_fills_bayar_and_enables_referensi_and_enter_there_saves() => WithDialog(Cart13680(), (window, _) =>
    {
        Find<ComboBox>(window, "MethodBox").SelectedItem = _api.Config.PaymentMethods.Single(m => m.Code == "QRIS");
        DoEvents();

        Assert.Equal("13.680", Find<TextBox>(window, "BayarBox").Text);
        var reference = Find<TextBox>(window, "ReferenceBox");
        Assert.True(reference.IsEnabled);
        reference.Focus();
        Type(reference, "QR998877");

        PressKey(reference, Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(("QRIS", 13680m, "QR998877"), (request.Payments[0].Method, request.Payments[0].Amount, request.Payments[0].Reference));
    });

    [Fact]
    public void Enter_in_keterangan_saves() => WithDialog(Cart13680(), (window, _) =>
    {
        Type(Find<TextBox>(window, "BayarBox"), "15000");
        var notes = Find<TextBox>(window, "NotesBox");
        notes.Focus();
        Type(notes, "titip");

        PressKey(notes, Key.Enter);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal("titip", request.Notes);
        Assert.Equal(15000m, request.Payments[0].Amount);
    });

    [Fact]
    public void Escape_closes_without_saving() => WithDialog(Cart13680(), (window, vm) =>
    {
        PressKey(Find<TextBox>(window, "BayarBox"), Key.Escape);

        Assert.False(vm.DialogResult);
        Assert.Empty(_api.SaleRequests);
    });

    [Fact]
    public void Sale_result_dialog_shows_nota_and_change_and_enter_closes() => OnSta(() =>
    {
        var result = new SaleResult(new SaleSummary(Guid.CreateVersion7(), "TK01-SL-202611-000031", 13680m, 0m, 20m, 13700m, 6300m, 0m), [], false);
        var vm = new SaleResultViewModel(result);
        var window = ShowOffScreen(new SaleResultDialog { DataContext = vm });

        Assert.Equal("TK01-SL-202611-000031", Find<TextBlock>(window, "NumberText").Text);
        Assert.Equal("6.300", Find<TextBlock>(window, "ChangeText").Text);

        PressKey(Find<Button>(window, "OkButton"), Key.Enter);

        Assert.False(window.IsVisible);
    });

    [Fact]
    public void F6_in_the_sales_form_opens_payment_and_clears_after_success() => OnSta(() =>
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create"));
        _api.Catalog.Add(Samples.Amplop);
        _dialogs.OnPayment = p => Samples.Saved(new CreateSaleRequest(p.SaleId, null, [], [], null, null, null, null, null),
            "TK01-SL-202611-000032", 7500m, 2500m);
        var vm = new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(),
            Receipts.Service(_api, _dialogs, _printer), (_, _) => Task.CompletedTask);
        var view = new SalesView { DataContext = vm };
        var window = Host(view);
        try
        {
            var search = Find<TextBox>(view, "SearchBox");
            search.Text = "13589";
            PressKey(search, Key.Enter);

            PressKey(search, Key.F6);

            Assert.Contains("payment", _dialogs.Asked);
            Assert.Empty(Find<DataGrid>(view, "CartGrid").Items);
            Assert.Equal("Nota TK01-SL-202611-000032 tersimpan. Kembali Rp 2.500.", Find<TextBlock>(view, "StatusText").Text);
        }
        finally
        {
            window.Close();
        }
    });
}
