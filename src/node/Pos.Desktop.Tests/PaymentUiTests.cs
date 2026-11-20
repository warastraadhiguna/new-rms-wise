using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using Pos.Desktop.Views.Dialogs;
using Pos.Shared.Sales;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>The payment dialog (BayarNotaForm) and F6 driven through real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class PaymentUiTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();

    private PaymentViewModel Vm(Cart cart, CustomerItem? customer = null) =>
        new(_api, _dialogs, _api.Config, cart, customer, Guid.CreateVersion7());

    private static Cart CartOf(params (ProductItem Item, decimal Qty)[] lines)
    {
        var cart = new Cart();
        foreach (var (item, qty) in lines)
            cart.Add(item.ToCartItem(), qty);
        return cart;
    }

    [Fact]
    public void Enter_walks_through_the_fields_in_rms2_order_and_saves_on_keterangan() => OnSta(() =>
    {
        _api.OnCreateSale = r => Samples.Saved(r, "TK01-SL-202611-000030", 13700m, 6300m);
        var vm = Vm(CartOf((Samples.Amplop, 1m), (Samples.Kantong, 1m), (Samples.Product(Guid.CreateVersion7(), "X1", "Permen", 1680m), 1m)));
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });

        Assert.Equal("13.680", Find<TextBox>(window, "GrandTotalBox").Text);
        Assert.Equal("13.700", Find<TextBox>(window, "TotalBox").Text);
        var method = Find<ComboBox>(window, "MethodBox");
        Assert.True(HasFocus(method));
        Assert.Equal("TUNAI", ((PaymentMethodInfo)method.SelectedItem).Name);

        PressKey(method, Key.Enter);
        Assert.True(HasFocus(Find<ComboBox>(window, "DiscountModeBox")));
        PressKey(Find<ComboBox>(window, "DiscountModeBox"), Key.Enter);
        Assert.True(HasFocus(Find<TextBox>(window, "DiscountBox")));
        PressKey(Find<TextBox>(window, "DiscountBox"), Key.Enter);

        var bayar = Find<TextBox>(window, "BayarBox");
        Assert.True(HasFocus(bayar));                         // read-only Total is skipped
        bayar.Text = "20.000";
        Assert.Equal("6.300", Find<TextBox>(window, "ChangeBox").Text);
        PressKey(bayar, Key.Enter);

        var reference = Find<TextBox>(window, "ReferenceBox");
        Assert.True(HasFocus(reference));                     // read-only Kembali is skipped
        PressKey(reference, Key.Enter);
        var notes = Find<TextBox>(window, "NotesBox");
        Assert.True(HasFocus(notes));
        notes.Text = "titip";
        Assert.Empty(_api.SaleRequests);

        PressKey(notes, Key.Enter);                          // last field = Simpan

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(("CASH", 20000m), (request.Payments[0].Method, request.Payments[0].Amount));
        Assert.Equal("titip", request.Notes);
        Assert.False(window.IsVisible);
        Assert.Equal("TK01-SL-202611-000030", vm.Result?.Sale.Number);
    });

    [Fact]
    public void Choosing_a_non_cash_method_fills_bayar_with_the_total() => OnSta(() =>
    {
        var vm = Vm(CartOf((Samples.Kantong, 3m)));
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            var method = Find<ComboBox>(window, "MethodBox");
            method.SelectedItem = vm.PaymentMethods.Single(m => m.Code == "QRIS");
            DoEvents();

            Assert.Equal("13.500", Find<TextBox>(window, "BayarBox").Text);
            Assert.Equal("0", Find<TextBox>(window, "ChangeBox").Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Potongan_rupiah_via_the_combo_box_and_value_box() => OnSta(() =>
    {
        var vm = Vm(CartOf((Samples.Kantong, 3m)));
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            Find<ComboBox>(window, "DiscountModeBox").SelectedIndex = 1;      // Potongan (Rp)
            Find<TextBox>(window, "DiscountBox").Text = "500";
            DoEvents();

            Assert.True(vm.IsNominalDiscount);
            Assert.Equal("13.000", Find<TextBox>(window, "TotalBox").Text);   // 13.500 - 500, already a multiple of 100
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Error_is_shown_and_the_dialog_stays_open() => OnSta(() =>
    {
        var vm = Vm(CartOf((Samples.Kantong, 1m)));
        var window = ShowOffScreen(new PaymentDialog { DataContext = vm });
        try
        {
            var bayar = Find<TextBox>(window, "BayarBox");
            bayar.Text = "1.000";
            Click(Find<Button>(window, "SaveButton"));

            Assert.Equal("Jumlah bayar kurang dari total.", Find<TextBlock>(window, "ErrorText").Text);
            Assert.True(window.IsVisible);
            Assert.True(HasFocus(bayar));
        }
        finally
        {
            window.Close();
        }
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
        var vm = new SalesViewModel(_api, session, _dialogs, new MemoryCartStore(), new MemoryViewSettingsStore(), (_, _) => Task.CompletedTask);
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
