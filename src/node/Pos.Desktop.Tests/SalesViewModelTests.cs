using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Tests;

public class SalesViewModelTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MemoryCartStore _cartStore = new();
    private readonly MemoryViewSettingsStore _viewStore = new();
    private readonly SessionState _session = new();
    private readonly List<(TimeSpan Delay, CancellationToken Token)> _delays = [];

    public SalesViewModelTests()
    {
        _session.SetLogin(Samples.LoginWith("sale.create"));
        _api.Catalog.AddRange([Samples.Amplop, Samples.Kantong, Samples.Voucher]);
    }

    private readonly FakePrinter _printer = new();
    private readonly MemoryLastSaleStore _lastSale = new();

    private SalesViewModel Vm(string? printerName = "EPSON TM-T82II Receipt") => new(_api, _session, _dialogs, _cartStore, _viewStore,
        Receipts.Service(_api, _dialogs, _printer, printerName, lastSale: _lastSale), (delay, token) =>
    {
        _delays.Add((delay, token));
        return Task.CompletedTask;
    });

    private static async Task Type(SalesViewModel vm, string text)
    {
        vm.SearchText = text;
        await vm.PendingSearch;
    }

    private static async Task Scan(SalesViewModel vm, string code, string qty = "1")
    {
        vm.QtyText = qty;
        vm.SearchText = code;
        await vm.SearchEnterCommand.ExecuteAsync(null);
    }

    // ---- search and add -----------------------------------------------------------------------

    [Fact]
    public async Task Typing_filters_the_product_grid_after_the_debounce()
    {
        var vm = Vm();

        await Type(vm, "amplop");

        Assert.Equal(TimeSpan.FromMilliseconds(300), Assert.Single(_delays).Delay);
        Assert.Equal("2023_amplop lebaran karakter", Assert.Single(vm.Products).Name);
        Assert.Same(vm.Products[0], vm.SelectedProduct);
    }

    [Fact]
    public async Task New_typing_cancels_the_previous_search()
    {
        var vm = Vm();

        vm.SearchText = "amp";
        vm.SearchText = "kantong";
        await vm.PendingSearch;

        Assert.True(_delays[0].Token.IsCancellationRequested);
        Assert.Equal("2_HD kantong hitam 3kg", Assert.Single(vm.Products).Name);
    }

    [Fact]
    public async Task Enter_with_an_exact_code_adds_that_item_with_the_x_qty_and_resets()
    {
        var vm = Vm();

        await Scan(vm, "13589", qty: "3");

        var line = Assert.Single(vm.Lines);
        Assert.Equal(1, line.No);
        Assert.Equal("13589", line.Code);
        Assert.Equal(3m, line.Qty);
        Assert.Equal(22500m, line.SubTotal);
        Assert.Equal(22500m, vm.Total);
        Assert.Equal("1", vm.QtyText);
        Assert.Equal("", vm.SearchText);
        Assert.Equal("search", vm.FocusRequest?.Target);
        Assert.Same(line, vm.SelectedLine);
    }

    [Fact]
    public async Task Enter_by_barcode_adds_the_exact_item()
    {
        var vm = Vm();

        await Scan(vm, Samples.Kantong.Barcode!);

        Assert.Equal("5113", Assert.Single(vm.Lines).Code);
    }

    [Fact]
    public async Task Enter_without_exact_match_adds_the_first_row()
    {
        var vm = Vm();

        await Scan(vm, "kantong");

        Assert.Equal("5113", Assert.Single(vm.Lines).Code);
    }

    [Fact]
    public async Task Unknown_code_shows_a_message()
    {
        var vm = Vm();

        await Scan(vm, "tidak-ada");

        Assert.Empty(vm.Lines);
        Assert.Equal("Barang tidak ditemukan: tidak-ada", vm.StatusMessage);
    }

    [Fact]
    public async Task Same_item_again_increases_the_existing_line()
    {
        var vm = Vm();

        await Scan(vm, "13589");
        await Scan(vm, "5113");
        await Scan(vm, "13589", qty: "2");

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal(3m, vm.Lines[0].Qty);
        Assert.Same(vm.Lines[0], vm.SelectedLine);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("")]
    public async Task Invalid_x_qty_is_refused(string qty)
    {
        var vm = Vm();

        await Scan(vm, "13589", qty);

        Assert.Empty(vm.Lines);
        Assert.Equal("Jumlah di kotak x tidak valid.", vm.StatusMessage);
    }

    [Fact]
    public async Task Enter_on_the_x_box_adds_the_search_text_with_that_qty()
    {
        var vm = Vm();
        vm.SearchText = "13589";
        vm.QtyText = "4";

        await vm.QtyEnterCommand.ExecuteAsync(null);

        Assert.Equal(4m, Assert.Single(vm.Lines).Qty);
    }

    [Fact]
    public async Task F1_picks_a_product_from_the_large_search_and_adds_it()
    {
        _dialogs.ProductToPick = Samples.Kantong;
        var vm = Vm();
        vm.SearchText = "kan";
        vm.QtyText = "2";

        vm.OpenProductSearchCommand.Execute(null);

        Assert.Contains("pick-product:kan", _dialogs.Asked);
        Assert.Equal(2m, Assert.Single(vm.Lines).Qty);
    }

    [Fact]
    public async Task Selected_row_of_the_product_grid_can_be_added()
    {
        var vm = Vm();
        await Type(vm, "a");
        vm.SelectedProduct = vm.Products.Single(p => p.Sku == "5113");

        await vm.AddSelectedProductCommand.ExecuteAsync(null);

        Assert.Equal("5113", Assert.Single(vm.Lines).Code);
    }

    // ---- multiplier, qty, unit ----------------------------------------------------------------

    [Fact]
    public async Task Star_starts_the_multiplier_for_the_last_line_and_enter_applies_it()
    {
        var vm = Vm();
        await Scan(vm, "13589");
        await Scan(vm, "5113");

        vm.StartMultiplierCommand.Execute(null);

        Assert.True(vm.IsMultiplierMode);
        Assert.Equal("", vm.QtyText);
        Assert.Equal("qty", vm.FocusRequest?.Target);

        vm.QtyText = "6";
        await vm.QtyEnterCommand.ExecuteAsync(null);

        Assert.False(vm.IsMultiplierMode);
        Assert.Equal(6m, vm.Lines[1].Qty);
        Assert.Equal(1m, vm.Lines[0].Qty);
        Assert.Equal("1", vm.QtyText);
        Assert.Equal("search", vm.FocusRequest?.Target);
    }

    [Fact]
    public async Task Star_uses_the_selected_line()
    {
        var vm = Vm();
        await Scan(vm, "13589");
        await Scan(vm, "5113");
        vm.SelectedLine = vm.Lines[0];

        vm.StartMultiplierCommand.Execute(null);
        vm.QtyText = "4";
        await vm.QtyEnterCommand.ExecuteAsync(null);

        Assert.Equal(4m, vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Star_is_refused_for_products_without_multiplier()
    {
        var vm = Vm();
        await Scan(vm, "VCR10");

        vm.StartMultiplierCommand.Execute(null);

        Assert.False(vm.IsMultiplierMode);
        Assert.Equal("Voucher pulsa 10rb harus discan satu per satu (tidak boleh memakai pengali).", vm.StatusMessage);
    }

    [Fact]
    public async Task Escape_leaves_multiplier_mode_without_closing()
    {
        var vm = Vm();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await Scan(vm, "13589");
        vm.StartMultiplierCommand.Execute(null);

        vm.EscapeCommand.Execute(null);

        Assert.False(vm.IsMultiplierMode);
        Assert.False(closed);
        Assert.Equal("1", vm.QtyText);
    }

    [Fact]
    public async Task F8_changes_the_qty_of_the_selected_line()
    {
        _dialogs.Quantity = 3m;
        var vm = Vm();
        await Scan(vm, "13589");

        vm.ChangeQtyCommand.Execute(null);

        Assert.Contains("qty:2023_amplop lebaran karakter:1", _dialogs.Asked);
        Assert.Equal(3m, vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Unit_of_a_line_can_be_changed()
    {
        _api.OnUnits = _ => [Samples.Amplop, Samples.AmplopPak];
        _dialogs.UnitToPick = units => units.Single(u => u.UnitCode == "PAK");
        var vm = Vm();
        await Scan(vm, "13589", qty: "2");

        await vm.ChangeUnitCommand.ExecuteAsync(vm.Lines[0]);

        Assert.Equal("PAK", vm.Lines[0].UnitCode);
        Assert.Equal(2m, vm.Lines[0].Qty);
        Assert.Equal(140000m, vm.Total);
    }

    // ---- delete with permission or supervisor --------------------------------------------------

    [Fact]
    public async Task Delete_with_permission_needs_no_supervisor()
    {
        _session.SetLogin(Samples.LoginWith("sale.create", "sale.line.delete"));
        var vm = Vm();
        await Scan(vm, "13589");
        await Scan(vm, "5113");

        await vm.DeleteLineCommand.ExecuteAsync(null);

        Assert.Equal("13589", Assert.Single(vm.Lines).Code);
        Assert.Empty(_dialogs.Asked);
        Assert.Equal(1, vm.Lines[0].No);
    }

    [Fact]
    public async Task Delete_without_permission_asks_a_supervisor_password()
    {
        _dialogs.Supervisor = new SupervisorCredentials("spv", "rahasia");
        _api.OnApprove = (action, user, _) => new ApprovalResult("tok", action, DateTime.UtcNow.AddMinutes(2), new UserInfo(Guid.CreateVersion7(), user, "Supervisor"));
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.DeleteLineCommand.ExecuteAsync(null);

        Assert.Contains("approve:sale.line.delete:spv", _api.Calls);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public async Task Wrong_supervisor_password_keeps_the_line()
    {
        _dialogs.Supervisor = new SupervisorCredentials("spv", "salah");
        _api.OnApprove = (_, _, _) => throw new NodeApiException("Username atau password supervisor salah.", "approval_rejected", 403);
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.DeleteLineCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
        Assert.Equal("Username atau password supervisor salah.", vm.StatusMessage);
    }

    [Fact]
    public async Task Cancelled_supervisor_dialog_keeps_the_line()
    {
        _dialogs.Supervisor = null;
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.DeleteLineCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("approve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ctrl_shift_o_unlocks_delete_for_this_transaction_only()
    {
        _dialogs.Supervisor = new SupervisorCredentials("spv", "rahasia");
        _dialogs.CancelReason = "Pelanggan batal";
        _api.OnApprove = (action, user, _) => new ApprovalResult("tok", action, DateTime.UtcNow.AddMinutes(2), new UserInfo(Guid.CreateVersion7(), user, "Supervisor"));
        _api.OnCancelCart = (id, reason, total, _) => new CartCancellationResult(id, "TK01-BT-202611-000001", reason, total);
        var vm = Vm();
        await Scan(vm, "13589");
        await Scan(vm, "5113");

        await vm.UnlockDeleteCommand.ExecuteAsync(null);
        Assert.True(vm.IsDeleteUnlocked);
        _dialogs.Asked.Clear();

        await vm.DeleteLineCommand.ExecuteAsync(null);
        await vm.DeleteLineCommand.ExecuteAsync(null);

        Assert.Empty(vm.Lines);
        Assert.DoesNotContain(_dialogs.Asked, a => a.StartsWith("supervisor", StringComparison.Ordinal));

        // a new transaction starts locked again
        await Scan(vm, "13589");
        await vm.CancelTransactionCommand.ExecuteAsync(null);
        Assert.False(vm.IsDeleteUnlocked);
    }

    // ---- F12 cancel, Esc close ----------------------------------------------------------------

    [Fact]
    public async Task F12_saves_a_cancellation_with_reason_and_clears_the_cart()
    {
        _dialogs.CancelReason = "Pelanggan batal membeli";
        IReadOnlyList<CartCancellationLine>? sent = null;
        _api.OnCancelCart = (id, reason, total, lines) =>
        {
            sent = lines;
            Assert.Equal(7, id.Version);
            Assert.Equal(19500m, total);
            return new CartCancellationResult(id, "TK01-BT-202611-000003", reason, total);
        };
        var vm = Vm();
        await Scan(vm, "13589", qty: "2");
        await Scan(vm, "5113");

        await vm.CancelTransactionCommand.ExecuteAsync(null);

        Assert.Equal(2, sent!.Count);
        Assert.Equal(2m, sent[0].Qty);
        Assert.Equal(7500m, sent[0].UnitPrice);
        Assert.Empty(vm.Lines);
        Assert.Equal(0m, vm.Total);
        Assert.Null(_cartStore.Saved);
        Assert.Equal("Transaksi dibatalkan (TK01-BT-202611-000003).", vm.StatusMessage);
        Assert.Equal("search", vm.FocusRequest?.Target);
    }

    [Fact]
    public async Task F12_on_an_empty_cart_or_without_reason_does_nothing()
    {
        var vm = Vm();
        await vm.CancelTransactionCommand.ExecuteAsync(null);
        Assert.Equal("Keranjang masih kosong.", vm.StatusMessage);

        await Scan(vm, "13589");
        _dialogs.CancelReason = null;
        await vm.CancelTransactionCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("cancel-cart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F12_failure_keeps_the_cart()
    {
        _dialogs.CancelReason = "Batal";
        _api.OnCancelCart = (_, _, _, _) => throw new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null);
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.CancelTransactionCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
        Assert.Equal("Node Service tidak dapat dihubungi.", vm.StatusMessage);
    }

    [Fact]
    public async Task Escape_closes_an_empty_form_without_asking()
    {
        var vm = Vm();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        await vm.EscapeCommand.ExecuteAsync(null);

        Assert.Equal(1, closed);
        Assert.Empty(_dialogs.Asked);
    }

    [Fact]
    public async Task Escape_with_items_needs_a_reason_and_saves_a_cancellation_like_f12()
    {
        var vm = Vm();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        _api.OnCancelCart = (id, reason, total, lines) => new CartCancellationResult(id, "TK01-BT-202611-000004", reason, total);
        await Scan(vm, "13589", qty: "2");

        _dialogs.CancelReason = null;                       // reason dialog cancelled: form stays
        await vm.EscapeCommand.ExecuteAsync(null);
        Assert.Equal(0, closed);
        Assert.Single(vm.Lines);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("cancel-cart", StringComparison.Ordinal));

        _dialogs.CancelReason = "Pelanggan pergi";
        await vm.EscapeCommand.ExecuteAsync(null);

        Assert.Contains("cancel-cart:Pelanggan pergi", _api.Calls);
        Assert.Equal(1, closed);
        Assert.Empty(vm.Lines);
        Assert.Null(_cartStore.Saved);
        Assert.DoesNotContain(_dialogs.Asked, a => a.StartsWith("confirm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Escape_keeps_the_form_open_when_the_cancellation_cannot_be_saved()
    {
        var vm = Vm();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        _dialogs.CancelReason = "Batal";
        _api.OnCancelCart = (_, _, _, _) => throw new NodeApiException("Node Service tidak dapat dihubungi.", "node_unreachable", null);
        await Scan(vm, "13589");

        await vm.EscapeCommand.ExecuteAsync(null);

        Assert.Equal(0, closed);
        Assert.Single(vm.Lines);
        Assert.Equal("Node Service tidak dapat dihubungi.", vm.StatusMessage);
    }

    // ---- warnings, persistence, view settings, member -----------------------------------------

    [Fact]
    public async Task Qty_above_sellable_stock_is_a_warning_not_a_block()
    {
        _api.Catalog.Add(Samples.Product(Guid.CreateVersion7(), "LOW", "Barang stok tipis", 1000m, stock: 2m));
        var vm = Vm();

        await Scan(vm, "LOW", qty: "3");

        Assert.Single(vm.Lines);
        Assert.Equal("Stok Barang stok tipis di lokasi jual tinggal 2, di keranjang 3. Transaksi tetap bisa dilanjutkan.", vm.WarningMessage);
    }

    [Fact]
    public async Task Cart_is_saved_after_every_change_and_restored()
    {
        var vm = Vm();
        await Scan(vm, "13589", qty: "2");
        vm.Customer = new CustomerItem(Guid.CreateVersion7(), "C000001", "Budi", null, 500000m, 0m, 500000m, 14, 0);

        var restored = Vm();

        Assert.Equal(2m, Assert.Single(restored.Lines).Qty);
        Assert.Equal("C000001", restored.Customer?.Code);
        Assert.Equal("Keranjang sebelumnya dipulihkan.", restored.StatusMessage);
    }

    [Fact]
    public void Column_choices_are_loaded_and_saved()
    {
        _viewStore.Settings = new SalesViewSettings(ShowBrand: true);
        var vm = Vm();

        Assert.True(vm.ShowBrand);
        Assert.True(vm.ShowUnit);
        vm.ShowStock = true;

        Assert.True(_viewStore.Settings.ShowStock);
        Assert.True(_viewStore.Settings.ShowBrand);
    }

    [Fact]
    public async Task Member_enter_with_one_match_selects_the_customer()
    {
        _api.Customers.Add(new CustomerItem(Guid.CreateVersion7(), "C000001", "Budi Pelanggan", null, 500000m, 0m, 500000m, 14, 0));
        var vm = Vm();
        vm.CustomerText = "C000001";

        await vm.MemberEnterCommand.ExecuteAsync(null);

        Assert.Equal("C000001", vm.Customer?.Code);
        Assert.Equal("C000001 - Budi Pelanggan", vm.CustomerText);
        Assert.Empty(_dialogs.Asked);
    }

    [Fact]
    public async Task Member_with_several_matches_opens_the_picker()
    {
        _api.Customers.Add(new CustomerItem(Guid.CreateVersion7(), "C1", "Budi A", null, null, 0m, null, null, 0));
        _api.Customers.Add(new CustomerItem(Guid.CreateVersion7(), "C2", "Budi B", null, null, 0m, null, null, 0));
        _dialogs.CustomerToPick = _api.Customers[1];
        var vm = Vm();
        vm.CustomerText = "budi";

        await vm.MemberEnterCommand.ExecuteAsync(null);

        Assert.Contains("pick-customer:budi", _dialogs.Asked);
        Assert.Equal("C2", vm.Customer?.Code);
    }

    // ---- F6 Bayar --------------------------------------------------------------------------------

    [Fact]
    public async Task F6_on_an_empty_cart_does_nothing()
    {
        var vm = Vm();

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Equal("Keranjang masih kosong.", vm.StatusMessage);
        Assert.Empty(_dialogs.Payments);
    }

    [Fact]
    public async Task F6_pays_then_shows_the_nota_clears_the_cart_and_focuses_cari()
    {
        _dialogs.OnPayment = p =>
        {
            Assert.Equal(7500m, p.Total);
            return Samples.Saved(new CreateSaleRequest(p.SaleId, null, [], [], null, null, null, null, null), "TK01-SL-202611-000021", 7500m, 2500m);
        };
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Contains("sales-config", _api.Calls);
        Assert.Equal("TK01-SL-202611-000021", Assert.Single(_dialogs.ShownResults).Sale.Number);
        Assert.Empty(vm.Lines);
        Assert.Null(_cartStore.Saved);
        Assert.Equal("Nota TK01-SL-202611-000021 tersimpan. Kembali Rp 2.500.", vm.StatusMessage);
        Assert.Equal("search", vm.FocusRequest?.Target);
    }

    private SaleResult PaidAmplop(ViewModels.PaymentViewModel p) =>
        Samples.Saved(new CreateSaleRequest(p.SaleId, null, [], [], null, null, null, null, null), "TK01-SL-202611-000021", 7500m, 2500m);

    [Fact]
    public async Task Paying_prints_the_receipt_automatically_from_the_stored_sale()
    {
        _dialogs.OnPayment = PaidAmplop;
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        var saleId = _dialogs.Payments[0].SaleId;
        Assert.Contains($"get-sale:{saleId}", _api.Calls);
        var job = Assert.Single(_printer.Jobs);
        Assert.Equal(Pos.Shared.Printing.EscPos.FeedAndCut, job.Data[^4..]);
        Assert.Equal(saleId, _lastSale.SaleId);
        Assert.True(_api.Calls.IndexOf($"get-sale:{saleId}") > _api.Calls.IndexOf("sales-config"));
        Assert.Equal(["payment", "sale-result"], _dialogs.Asked.Where(a => a is "payment" or "sale-result" or "receipt-preview"));
    }

    [Fact]
    public async Task Printer_failure_does_not_undo_the_sale()
    {
        _printer.Failure = "printer offline.";
        _dialogs.OnPayment = PaidAmplop;
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.ShownResults);                 // nota and Kembali still shown
        Assert.Empty(vm.Lines);                               // cart cleared: the sale is saved
        Assert.Null(_cartStore.Saved);
        Assert.Single(_dialogs.Previews);                     // receipt on screen instead
        Assert.Equal(_dialogs.Payments[0].SaleId, _lastSale.SaleId);   // F5 can still print it later
        Assert.StartsWith("Nota TK01-SL-202611-000021 tersimpan.", vm.StatusMessage);
        Assert.Contains("Gagal mencetak", vm.StatusMessage);
    }

    [Fact]
    public async Task Unexpected_print_error_does_not_undo_the_sale_either()
    {
        _api.OnGetSale = _ => throw new HttpRequestException("socket closed");
        _dialogs.OnPayment = PaidAmplop;
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.ShownResults);
        Assert.Empty(vm.Lines);
        Assert.Contains("Struk gagal dicetak: socket closed", vm.StatusMessage);
        Assert.Contains("F5", vm.StatusMessage);
    }

    [Fact]
    public async Task Without_a_printer_paying_shows_the_receipt_preview()
    {
        _dialogs.OnPayment = PaidAmplop;
        var vm = Vm(printerName: null);
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Empty(_printer.Jobs);
        Assert.Contains("printer_name", Assert.Single(_dialogs.Previews).Message);
        Assert.Single(_dialogs.ShownResults);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public async Task F5_without_a_previous_nota_says_so()
    {
        var vm = Vm();

        await vm.ReprintCommand.ExecuteAsync(null);

        Assert.Equal("Belum ada nota di perangkat ini untuk dicetak ulang.", vm.StatusMessage);
        Assert.Empty(_printer.Jobs);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("get-sale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F5_reloads_the_last_nota_from_the_node_service_and_prints_cetak_ulang()
    {
        var saleId = Guid.CreateVersion7();
        _lastSale.SaleId = saleId;
        var vm = Vm();

        await vm.ReprintCommand.ExecuteAsync(null);
        await vm.ReprintCommand.ExecuteAsync(null);

        Assert.Equal(2, _api.Calls.Count(c => c == $"get-sale:{saleId}"));     // fetched again each time, never cached
        Assert.All(_printer.Jobs, j => Assert.Contains("CETAK ULANG", System.Text.Encoding.ASCII.GetString(j.Data)));
        Assert.Equal("Nota TK01-SL-202611-000123 dicetak ulang.", vm.StatusMessage);
        Assert.Equal("search", vm.FocusRequest?.Target);
    }

    [Fact]
    public async Task Closing_the_payment_keeps_the_cart_and_the_same_sale_id_for_the_next_try()
    {
        _dialogs.OnPayment = _ => null;
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);
        await vm.PayCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
        Assert.Equal(_dialogs.Payments[0].SaleId, _dialogs.Payments[1].SaleId);
        Assert.Equal(_dialogs.Payments[0].SaleId, _cartStore.Saved?.PendingSaleId);
    }

    [Fact]
    public async Task Changing_the_cart_after_a_failed_payment_uses_a_new_sale_id()
    {
        _dialogs.OnPayment = _ => null;
        var vm = Vm();
        await Scan(vm, "13589");
        await vm.PayCommand.ExecuteAsync(null);

        await Scan(vm, "5113");
        await vm.PayCommand.ExecuteAsync(null);

        Assert.NotEqual(_dialogs.Payments[0].SaleId, _dialogs.Payments[1].SaleId);
    }

    [Fact]
    public async Task Pending_sale_id_survives_a_restart()
    {
        _dialogs.OnPayment = _ => null;
        var vm = Vm();
        await Scan(vm, "13589");
        await vm.PayCommand.ExecuteAsync(null);

        var restarted = Vm();
        await restarted.PayCommand.ExecuteAsync(null);

        Assert.Equal(_dialogs.Payments[0].SaleId, _dialogs.Payments[1].SaleId);
    }

    [Fact]
    public async Task Credit_limit_warning_is_shown_in_indonesian()
    {
        _dialogs.OnPayment = p => Samples.Saved(new CreateSaleRequest(p.SaleId, null, [], [], null, null, null, null, null),
            "TK01-SL-202611-000022", 7500m, 0m, new SaleWarningInfo("credit_limit_exceeded", "Customer credit exceeds the limit."));
        var vm = Vm();
        await Scan(vm, "13589");

        await vm.PayCommand.ExecuteAsync(null);

        Assert.Equal("Kredit pelanggan melebihi limit.", vm.WarningMessage);
    }

    [Fact]
    public void Clearing_the_member_box_removes_the_customer()
    {
        var vm = Vm();
        vm.Customer = new CustomerItem(Guid.CreateVersion7(), "C1", "Budi", null, null, 0m, null, null, 0);

        vm.CustomerText = "";

        Assert.Null(vm.Customer);
    }
}
