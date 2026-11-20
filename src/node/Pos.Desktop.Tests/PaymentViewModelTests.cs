using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Shared.Sales;

namespace Pos.Desktop.Tests;

/// <summary>"Form Bayar Nota" as in RMS 2: Total = cart, Grand Total = after Potongan/Diskon and cash rounding.</summary>
public class PaymentViewModelTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Cart _cart = new();
    private readonly Guid _saleId = Guid.CreateVersion7();

    public PaymentViewModelTests()
    {
        // 7500 + 4500 + 1680 = 13680 -> TUNAI grand total 13700 (nearest 100)
        _cart.Add(Samples.Amplop.ToCartItem(), 1m);
        _cart.Add(Samples.Kantong.ToCartItem(), 1m);
        _cart.Add(Samples.Product(Guid.CreateVersion7(), "X1", "Permen", 1680m).ToCartItem(), 1m);
        _api.OnCreateSale = r => Samples.Saved(r, "TK01-SL-202611-000010", 13700m, 6300m);
    }

    private PaymentViewModel Vm(CustomerItem? customer = null) => new(_api, _dialogs, _api.Config, _cart, customer, _saleId);

    private static PaymentMethodInfo Method(PaymentViewModel vm, string code) => vm.PaymentMethods.Single(m => m.Code == code);

    [Fact]
    public void Opens_like_rms2_tunai_potongan_and_focus_on_bayar()
    {
        var vm = Vm();

        Assert.Equal(13680m, vm.Total);
        Assert.Equal(13700m, vm.GrandTotal);
        Assert.Equal("TUNAI", vm.SelectedMethod?.Name);
        Assert.Equal(["Potongan", "Diskon"], vm.DiscountModes);
        Assert.Equal(0, vm.DiscountModeIndex);
        Assert.True(vm.IsNominalDiscount);
        Assert.Equal("", vm.BayarText);
        Assert.Equal(0m, vm.Change);
        Assert.Equal("bayar", vm.FocusRequest?.Target);
        Assert.False(vm.IsReferenceEnabled);
    }

    [Fact]
    public void Cash_change_is_calculated_while_typing()
    {
        var vm = Vm();

        vm.BayarText = "20.000";

        Assert.Equal(6300m, vm.Change);
    }

    [Theory]
    [InlineData("DEBIT", true)]
    [InlineData("QRIS", true)]
    [InlineData("TRANSFER", true)]
    [InlineData("CASH", false)]
    [InlineData("CREDIT", false)]
    public void Referensi_is_only_for_debit_qris_and_transfer(string code, bool enabled)
    {
        var vm = Vm();

        vm.SelectedMethod = Method(vm, code);

        Assert.Equal(enabled, vm.IsReferenceEnabled);
    }

    [Fact]
    public void Switching_back_to_tunai_clears_the_reference()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.ReferenceText = "QR-123";

        vm.SelectedMethod = Method(vm, "CASH");

        Assert.Equal("", vm.ReferenceText);
    }

    [Fact]
    public void Non_cash_bayar_is_filled_with_the_unrounded_grand_total()
    {
        var vm = Vm();

        vm.SelectedMethod = Method(vm, "QRIS");

        Assert.Equal(13680m, vm.GrandTotal);
        Assert.Equal("13.680", vm.BayarText);
        Assert.Equal(0m, vm.Change);
    }

    [Fact]
    public void Potongan_rupiah_is_the_default()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");

        vm.DiscountText = "1.000";

        Assert.Equal(1000m, vm.DiscountAmount);
        Assert.Equal(12680m, vm.GrandTotal);
        Assert.Equal("12.680", vm.BayarText);
    }

    [Fact]
    public void Diskon_percent_when_chosen()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");

        vm.DiscountModeIndex = 1;
        vm.DiscountText = "10";

        Assert.False(vm.IsNominalDiscount);
        Assert.Equal(1368m, vm.DiscountAmount);
        Assert.Equal(12312m, vm.GrandTotal);
    }

    [Fact]
    public void Invalid_discount_shows_an_error()
    {
        var vm = Vm();

        vm.DiscountText = "abc";

        Assert.Equal("Diskon tidak valid.", vm.ErrorMessage);
        Assert.Equal(0m, vm.DiscountAmount);
    }

    [Fact]
    public async Task Save_cash_sends_the_sale_with_the_client_id_and_returns_the_result()
    {
        var vm = Vm();
        vm.BayarText = "20000";
        vm.NotesText = "pelanggan tetap";

        await vm.SaveCommand.ExecuteAsync(null);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(_saleId, request.Id);
        Assert.Equal(3, request.Lines.Count);
        Assert.Equal(Samples.Amplop.ProductUnitId, request.Lines[0].ProductUnitId);
        var payment = Assert.Single(request.Payments);
        Assert.Equal(("CASH", 20000m, (string?)null), (payment.Method, payment.Amount, payment.Reference));
        Assert.Equal("pelanggan tetap", request.Notes);
        Assert.Null(request.DiscountPct);
        Assert.True(vm.DialogResult);
        Assert.Equal("TK01-SL-202611-000010", vm.Result?.Sale.Number);
    }

    [Fact]
    public async Task Reference_is_sent_for_qris()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.ReferenceText = "QR-998877";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("QR-998877", Assert.Single(_api.SaleRequests).Payments[0].Reference);
    }

    [Fact]
    public async Task Insufficient_cash_is_refused_before_calling_the_server()
    {
        var vm = Vm();
        vm.BayarText = "10000";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Jumlah bayar kurang dari total.", vm.ErrorMessage);
        Assert.Equal("bayar", vm.FocusRequest?.Target);
    }

    [Fact]
    public async Task Credit_needs_a_member()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "CREDIT");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Pembayaran kredit wajib memilih member/pelanggan.", vm.ErrorMessage);
        Assert.Empty(_api.SaleRequests);
    }

    [Fact]
    public async Task Credit_with_member_sends_the_customer()
    {
        var member = new CustomerItem(Guid.CreateVersion7(), "C000001", "Budi", null, 500000m, 0m, 500000m, 14, 0);
        var vm = Vm(member);
        vm.SelectedMethod = Method(vm, "CREDIT");

        await vm.SaveCommand.ExecuteAsync(null);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(member.Id, request.CustomerId);
        Assert.Equal(("CREDIT", 13680m), (request.Payments[0].Method, request.Payments[0].Amount));
    }

    [Fact]
    public async Task Discount_above_the_limit_asks_the_supervisor_password()
    {
        _dialogs.Supervisor = new SupervisorCredentials("spv", "rahasia");
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.DiscountModeIndex = 1;
        vm.DiscountText = "25";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains(_dialogs.Asked, a => a.StartsWith("supervisor:Diskon melebihi batas Anda (maks 10%)", StringComparison.Ordinal));
        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(25m, request.DiscountPct);
        Assert.Equal(("spv", "rahasia"), (request.SupervisorUsername, request.SupervisorPassword));
    }

    [Fact]
    public async Task Potongan_above_the_limit_also_needs_a_supervisor()
    {
        _dialogs.Supervisor = new SupervisorCredentials("spv", "rahasia");
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.DiscountText = "5.000";   // 36% of 13.680, limit 10%

        await vm.SaveCommand.ExecuteAsync(null);

        var request = Assert.Single(_api.SaleRequests);
        Assert.Equal(5000m, request.DiscountAmount);
        Assert.Equal("spv", request.SupervisorUsername);
    }

    [Fact]
    public async Task Discount_within_the_limit_needs_no_supervisor()
    {
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.DiscountModeIndex = 1;
        vm.DiscountText = "10";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_dialogs.Asked, a => a.StartsWith("supervisor", StringComparison.Ordinal));
        Assert.Null(Assert.Single(_api.SaleRequests).SupervisorUsername);
    }

    [Fact]
    public async Task Cancelled_supervisor_dialog_does_not_save()
    {
        _dialogs.Supervisor = null;
        var vm = Vm();
        vm.SelectedMethod = Method(vm, "QRIS");
        vm.DiscountModeIndex = 1;
        vm.DiscountText = "25";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_api.SaleRequests);
        Assert.Equal("Diskon ini perlu persetujuan supervisor.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Network_failure_keeps_the_dialog_and_resends_with_the_same_id()
    {
        var attempts = 0;
        _api.OnCreateSale = r => ++attempts == 1
            ? throw new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null)
            : Samples.Saved(r, "TK01-SL-202611-000011", 13700m, 6300m);
        var vm = Vm();
        vm.BayarText = "20000";

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.DialogResult);
        Assert.StartsWith("Node Service tidak dapat dihubungi.", vm.ErrorMessage);
        Assert.Contains("mengirim ulang", vm.ErrorMessage);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, _api.SaleRequests.Count);
        Assert.All(_api.SaleRequests, r => Assert.Equal(_saleId, r.Id));
        Assert.True(vm.DialogResult);
    }

    [Fact]
    public async Task Server_refusal_is_shown()
    {
        _api.OnCreateSale = _ => throw new NodeApiException("Barang ini harus discan satu per satu (tidak boleh memakai pengali).", "qty_multiplier_not_allowed", 422);
        var vm = Vm();
        vm.BayarText = "20000";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Barang ini harus discan satu per satu (tidak boleh memakai pengali).", vm.ErrorMessage);
        Assert.Null(vm.DialogResult);
    }
}
