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
    [InlineData("CREDIT", 13680)]
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
        }, customer: method == "CREDIT" ? CreditCustomer : null);

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
