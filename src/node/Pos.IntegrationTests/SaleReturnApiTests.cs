using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Sync;
using Pos.Shared.Time;

namespace Pos.IntegrationTests;

/// <summary>Retur penjualan (10c): quantities, price, conditions, settlements, window, Tutup Shift, Kartu Stok, idempotency.</summary>
[Collection(DatabaseCollection.Name)]
public class SaleReturnApiTests(DatabaseFixture db) : IAsyncLifetime
{
    private static readonly DateOnly E1 = new(2030, 1, 1);
    private static readonly DateOnly E2 = new(2030, 6, 1);
    private static int _users;

    private NodeServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.IsConfigured)
        {
            _factory = new NodeServiceFactory(db);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private sealed record User(Guid Id, string Name, string Token);

    private async Task<User> NewUserAsync(params string[] permissions)
    {
        var name = $"sr{Interlocked.Increment(ref _users)}_{Guid.NewGuid():N}"[..30];
        var id = await db.CreateUserAsync(name, "secret-1", roles: (JsonSerializer.Serialize(permissions), db.NodeId));
        return new User(id, name, db.TokenFor(id, name, permissions));
    }

    private Task<User> NewCashierAsync() => NewUserAsync("shift.open", "sale.create", "discount.max:100");

    private async Task<(Guid Device, Guid Shift)> OpenShiftAsync(User cashier, decimal openingCash = 100000m)
    {
        var device = await db.CreateDeviceAsync();
        var (status, body) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = openingCash }, cashier.Token, device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (device, body.Id());
    }

    private async Task<JsonElement> SellAsync(User cashier, Guid device, (TestProduct Product, decimal Qty)[] lines, object[] payments,
        Guid? customer = null, decimal? discountAmount = null)
    {
        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            DiscountAmount = discountAmount,
            Lines = lines.Select(l => new { l.Product.ProductUnitId, l.Qty }).ToArray(),
            Payments = payments,
        }, cashier.Token, device);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return body.GetProperty("sale");
    }

    private static object Pay(string method, decimal amount) => new { Method = method, Amount = amount };

    private async Task<Guid> ReasonAsync(string name = "Barang rusak")
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<Guid>("SELECT id FROM reasons WHERE type = 'RETURN' AND name = @name", new { name });
    }

    private async Task<(Guid SaleLineId, decimal Returnable, decimal NetUnitPrice)[]> SourceAsync(User user, Guid saleId)
    {
        var (status, body) = await _client.GetAsync($"/api/v1/returns/source?sale_id={saleId}", user.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("lines").EnumerateArray()
            .Select(l => (l.Id("sale_line_id"), l.Dec("returnable_qty"), l.Dec("net_unit_price"))).ToArray();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReturnAsync(User user, Guid device, Guid saleId, string settlement,
        (Guid SaleLineId, decimal Qty, string Condition)[] lines, Guid? id = null, string? approvalToken = null) =>
        await _client.PostAsync("/api/v1/returns", new
        {
            Id = id ?? Guid.CreateVersion7(),
            SaleId = saleId,
            Settlement = settlement,
            ReasonId = await ReasonAsync(),
            Notes = "uji retur",
            Lines = lines.Select(l => new { l.SaleLineId, l.Qty, l.Condition }).ToArray(),
            ApprovalToken = approvalToken,
        }, user.Token, device);

    private async Task<decimal> BalanceAsync(Guid product, Guid location, DateOnly? expiry = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE(SUM(base_qty), 0) FROM stock_balances WHERE product_id = @product AND location_id = @location AND (@expiry IS NULL OR expiry_key = @expiry)",
            new { product, location, expiry = expiry?.ToDateTime(TimeOnly.MinValue) });
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<Guid> DamagedLocationAsync() =>
        await ScalarAsync<Guid>("SELECT id FROM locations WHERE node_id = @NodeId AND code = 'RUSAK'", new { db.NodeId });

    // ---- tests --------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Partial_cash_return_puts_stock_back_per_expiry_at_the_net_price_and_reduces_the_shifts_cash()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 100000m);
        var tea = await db.CreateSellableProductAsync(10000m, trackExpiry: true, stock: [(E1, 2m), (E2, 10m)]);
        var water = await db.CreateSellableProductAsync(10000m, stock: [(null, 10m)]);
        // 3 tea (E1 2 + E2 1) + 3 water = 60.000, Potongan 6.000 -> net 9.000 per unit
        var sale = await SellAsync(cashier, device, [(tea, 3m), (water, 3m)], [Pay("CASH", 54000m)], discountAmount: 6000m);
        var source = await SourceAsync(cashier, sale.Id());
        Assert.Equal((3m, 9000m), (source[0].Returnable, source[0].NetUnitPrice));

        var (status, body) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(source[0].SaleLineId, 2m, "GOOD")]);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Matches(@"^TK01-RT-\d{6}-\d{6}$", body.Str("number"));
        Assert.Equal((18000m, 18000m, "REFUND"), (body.Dec("total"), body.Dec("refund_amount"), body.Str("settlement")));
        var line = body.GetProperty("lines")[0];
        Assert.Equal((2m, 9000m, 18000m, "GOOD", "DISPLAY"), (line.Dec("qty"), line.Dec("unit_price"), line.Dec("line_total"), line.Str("condition"), line.Str("location_code")));
        Assert.Equal([(E1, 2m)], line.GetProperty("expiries").EnumerateArray().Select(e => (DateOnly.Parse(e.Str("expiry_date")), e.Dec("base_qty"))));
        Assert.Equal((2m, 9m), (await BalanceAsync(tea.ProductId, db.LocationA, E1), await BalanceAsync(tea.ProductId, db.LocationA, E2)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_type = @type AND doc_id = @id",
            new { type = SyncDocTypes.SaleReturn, id = body.Id() }));
        Assert.Equal(1m, (await SourceAsync(cashier, sale.Id()))[0].Returnable);

        // the last tea goes back to E2 (E1 is fully returned)
        var (_, second) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(source[0].SaleLineId, 1m, "GOOD")]);
        Assert.Equal([(E2, 1m)], second.GetProperty("lines")[0].GetProperty("expiries").EnumerateArray()
            .Select(e => (DateOnly.Parse(e.Str("expiry_date")), e.Dec("base_qty"))));

        // Data Penjualan: Ada Retur; Kartu Stok: "Retur" with the RT number
        var (_, list) = await _client.GetAsync($"/api/v1/sales?q={sale.Str("number")}", cashier.Token);
        Assert.Equal("RETURNED", list.GetProperty("items")[0].Str("status"));
        var viewer = await NewUserAsync("stock.view");
        var (_, card) = await _client.GetAsync($"/api/v1/stock/card?product_id={tea.ProductId}&location_id={db.LocationA}", viewer.Token);
        Assert.Contains(card.GetProperty("rows").EnumerateArray(), r => r.Str("doc_label") == "Retur" && r.Str("doc_number") == body.Str("number"));

        // Tutup Shift: 100.000 + 54.000 cash - 27.000 refunds = 127.000
        var (_, closed) = await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 127000m }, cashier.Token, device);
        Assert.Equal((27000m, 127000m, 0m), (closed.Dec("cash_refunds"), closed.Dec("expected_cash"), closed.Dec("difference")));
    }

    [DatabaseFact]
    public async Task Returning_more_than_sold_minus_returned_is_refused()
    {
        var cashier = await NewCashierAsync();
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, [(product, 3m)], [Pay("CASH", 15000m)]);
        var lineId = (await SourceAsync(cashier, sale.Id()))[0].SaleLineId;

        var first = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 2m, "GOOD")]);
        var tooMany = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 2m, "GOOD")]);
        var zero = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 0m, "GOOD")]);
        var fraction = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 0.5m, "GOOD")]);
        var rest = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")]);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exceeds_returnable"), (tooMany.Status, tooMany.Body.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_return_qty"), (zero.Status, zero.Body.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_return_qty"), (fraction.Status, fraction.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Created, rest.Status);
        Assert.Equal(15000m, first.Body.Dec("total") + rest.Body.Dec("total"));
        Assert.Equal(0m, (await SourceAsync(cashier, sale.Id()))[0].Returnable);
    }

    [DatabaseFact]
    public async Task Damaged_goods_go_to_the_damaged_location()
    {
        var cashier = await NewCashierAsync();
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, trackExpiry: true, stock: [(E1, 5m)]);
        var sale = await SellAsync(cashier, device, [(product, 2m)], [Pay("CASH", 10000m)]);
        var lineId = (await SourceAsync(cashier, sale.Id()))[0].SaleLineId;
        var damaged = await DamagedLocationAsync();

        var (status, body) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 1m, "DAMAGED")]);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(("DAMAGED", "RUSAK"), (body.GetProperty("lines")[0].Str("condition"), body.GetProperty("lines")[0].Str("location_code")));
        Assert.Equal(1m, await BalanceAsync(product.ProductId, damaged, E1));
        Assert.Equal(3m, await BalanceAsync(product.ProductId, db.LocationA));                 // display unchanged by the return
    }

    [DatabaseFact]
    public async Task Potong_piutang_reduces_what_is_owed_only_on_credit_notas()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 20m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m);
        var credit = await SellAsync(cashier, device, [(product, 6m)], [Pay("CASH", 15000m), Pay("CREDIT", 45000m)], customer);
        var cash = await SellAsync(cashier, device, [(product, 1m)], [Pay("CASH", 10000m)]);
        var creditLine = (await SourceAsync(cashier, credit.Id()))[0].SaleLineId;
        var cashLine = (await SourceAsync(cashier, cash.Id()))[0].SaleLineId;

        var reduce = await ReturnAsync(cashier, device, credit.Id(), "REDUCE_RECEIVABLE", [(creditLine, 2m, "GOOD")]);
        var tooMuch = await ReturnAsync(cashier, device, credit.Id(), "REDUCE_RECEIVABLE", [(creditLine, 4m, "GOOD")]);
        var notCredit = await ReturnAsync(cashier, device, cash.Id(), "REDUCE_RECEIVABLE", [(cashLine, 1m, "GOOD")]);

        Assert.Equal((HttpStatusCode.Created, 0m), (reduce.Status, reduce.Body.Dec("refund_amount")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exceeds_receivable"), (tooMuch.Status, tooMuch.Body.Str("code")));   // 40.000 > 25.000
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "not_a_credit_sale"), (notCredit.Status, notCredit.Body.Str("code")));
        var (_, list) = await _client.GetAsync($"/api/v1/sales?q={credit.Str("number")}", cashier.Token);
        Assert.Equal(("UNPAID", 25000m), (list.GetProperty("items")[0].Str("status"), list.GetProperty("items")[0].Dec("outstanding")));
        var code = await ScalarAsync<string>("SELECT code FROM contacts WHERE id = @customer", new { customer });
        var (_, customers) = await _client.GetAsync($"/api/v1/customers/search?q={code}", cashier.Token);
        Assert.Equal(1000000m - 25000m, customers[0].Dec("credit_remaining"));
        var (_, closed) = await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 0m }, cashier.Token, device);
        Assert.Equal(0m, closed.Dec("cash_refunds"));                                       // no cash went out
    }

    [DatabaseFact]
    public async Task After_the_window_a_supervisor_approves()
    {
        var cashier = await NewCashierAsync();
        var supervisor = await NewUserAsync("sale.return.late");
        var lateUser = await NewUserAsync("shift.open", "sale.create", "sale.return.late");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, [(product, 3m)], [Pay("CASH", 15000m)]);
        var tenDaysAgo = DateTime.UtcNow.AddDays(-10);
        await using (var connection = await db.Factory.OpenAsync())
            await connection.ExecuteAsync("UPDATE sales SET sold_at = @tenDaysAgo WHERE id = @id", new { tenDaysAgo, id = sale.Id() });
        var (_, sourceBody) = await _client.GetAsync($"/api/v1/returns/source?number={sale.Str("number")}", cashier.Token);
        Assert.False(sourceBody.GetProperty("within_window").GetBoolean());
        Assert.Equal(7, sourceBody.GetProperty("window_days").GetInt32());
        var lineId = sourceBody.GetProperty("lines")[0].Id("sale_line_id");

        var (refused, refusedBody) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")]);
        var (_, approval) = await _client.PostAsync("/api/v1/auth/approve",
            new { Action = "sale.return.late", Username = supervisor.Name, Password = "secret-1" }, cashier.Token);
        var id = Guid.CreateVersion7();
        var (approved, approvedBody) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")], id,
            approval.Str("approval_token"));
        var (own, _) = await ReturnAsync(lateUser, device, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")]);

        Assert.Equal((HttpStatusCode.Forbidden, "return_approval_required"), (refused, refusedBody.Str("code")));
        Assert.Equal(HttpStatusCode.Created, approved);
        Assert.Equal(supervisor.Id, approvedBody.Id("approved_by"));
        Assert.Equal(HttpStatusCode.Created, own);
    }

    [DatabaseFact]
    public async Task A_cash_refund_needs_an_open_shift_on_the_device_but_an_exchange_does_not()
    {
        var cashier = await NewCashierAsync();
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, [(product, 2m)], [Pay("CASH", 10000m)]);
        var lineId = (await SourceAsync(cashier, sale.Id()))[0].SaleLineId;
        var otherDevice = await db.CreateDeviceAsync();

        var (refund, refundBody) = await ReturnAsync(cashier, otherDevice, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")]);
        var (exchange, exchangeBody) = await ReturnAsync(cashier, otherDevice, sale.Id(), "EXCHANGE", [(lineId, 1m, "GOOD")]);

        Assert.Equal((HttpStatusCode.Conflict, "no_open_shift"), (refund, refundBody.Str("code")));
        Assert.Equal(HttpStatusCode.Created, exchange);
        Assert.Equal(JsonValueKind.Null, exchangeBody.GetProperty("shift_id").ValueKind);
    }

    [DatabaseFact]
    public async Task Tukar_barang_pays_a_new_sale_once()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 50000m);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 20m)]);
        var other = await db.CreateSellableProductAsync(25000m, stock: [(null, 5m)]);
        var sale = await SellAsync(cashier, device, [(product, 2m)], [Pay("CASH", 20000m)]);
        var lineId = (await SourceAsync(cashier, sale.Id()))[0].SaleLineId;
        var (_, ret) = await ReturnAsync(cashier, device, sale.Id(), "EXCHANGE", [(lineId, 2m, "GOOD")]);   // value 20.000

        async Task<(HttpStatusCode Status, JsonElement Body)> Exchange(decimal returnAmount, decimal cash, TestProduct item) =>
            await _client.PostAsync("/api/v1/sales", new
            {
                Id = Guid.CreateVersion7(),
                Lines = new[] { new { item.ProductUnitId, Qty = 1m } },
                Payments = cash > 0 ? new[] { Pay("RETURN", returnAmount), Pay("CASH", cash) } : new[] { Pay("RETURN", returnAmount) },
                ExchangeReturnId = ret.Id(),
            }, cashier.Token, device);

        var smaller = await Exchange(20000m, 0m, product);                                    // 10.000 < 20.000
        var mismatch = await Exchange(15000m, 10000m, other);
        var (ok, okBody) = await Exchange(20000m, 5000m, other);                             // 25.000 = 20.000 retur + 5.000 cash
        var again = await Exchange(20000m, 5000m, other);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "non_cash_overpayment"), (smaller.Status, smaller.Body.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exchange_amount_mismatch"), (mismatch.Status, mismatch.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Created, ok);
        Assert.Equal((HttpStatusCode.Conflict, "exchange_already_used"), (again.Status, again.Body.Str("code")));

        var (_, detail) = await _client.GetAsync($"/api/v1/returns/{ret.Id()}", cashier.Token);
        Assert.Equal(okBody.GetProperty("sale").Str("number"), detail.Str("exchange_sale_number"));
        var (_, returns) = await _client.GetAsync("/api/v1/returns", cashier.Token);
        Assert.True(returns.EnumerateArray().Single(r => r.Id() == ret.Id()).GetProperty("exchange_used").GetBoolean());

        // Tutup Shift: cash = 50.000 + 20.000 + 5.000; RETUR shown as a non-cash method; no refund
        var (_, closed) = await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 75000m }, cashier.Token, device);
        Assert.Equal((75000m, 0m), (closed.Dec("expected_cash"), closed.Dec("cash_refunds")));
        Assert.Contains(closed.GetProperty("non_cash").EnumerateArray(), m => m.Str("code") == "RETURN" && m.Dec("amount") == 20000m);
    }

    [DatabaseFact]
    public async Task Same_id_returns_the_stored_return_and_parallel_requests_store_one()
    {
        var cashier = await NewCashierAsync();
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, [(product, 3m)], [Pay("CASH", 15000m)]);
        var lineId = (await SourceAsync(cashier, sale.Id()))[0].SaleLineId;
        var id = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 2m, "GOOD")], id)));

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body.ToString()));
        Assert.Single(results.Select(r => r.Body.Str("number")).Distinct());
        Assert.Equal(9m, await BalanceAsync(product.ProductId, db.LocationA));               // 10 - 3 + 2, once
    }

    [DatabaseFact]
    public async Task Voided_notas_cannot_be_returned_and_returned_notas_cannot_be_voided()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var voided = await SellAsync(cashier, device, [(product, 1m)], [Pay("CASH", 5000m)]);
        var returned = await SellAsync(cashier, device, [(product, 1m)], [Pay("CASH", 5000m)]);
        var voidReason = await ScalarAsync<Guid>("SELECT id FROM reasons WHERE type = 'SALE_VOID' LIMIT 1", new { });
        await _client.PostAsync($"/api/v1/sales/{voided.Id()}/void", new { Id = Guid.CreateVersion7(), ReasonId = voidReason, Notes = "x" }, cashier.Token);
        var voidedLine = (await SourceAsync(cashier, voided.Id()))[0].SaleLineId;
        var returnedLine = (await SourceAsync(cashier, returned.Id()))[0].SaleLineId;
        await ReturnAsync(cashier, device, returned.Id(), "REFUND", [(returnedLine, 1m, "GOOD")]);

        var (returnStatus, returnBody) = await ReturnAsync(cashier, device, voided.Id(), "REFUND", [(voidedLine, 1m, "GOOD")]);
        var (voidStatus, voidBody) = await _client.PostAsync($"/api/v1/sales/{returned.Id()}/void",
            new { Id = Guid.CreateVersion7(), ReasonId = voidReason, Notes = "x" }, cashier.Token);

        Assert.Equal((HttpStatusCode.Conflict, "sale_voided"), (returnStatus, returnBody.Str("code")));
        Assert.Equal((HttpStatusCode.Conflict, "has_returns"), (voidStatus, voidBody.Str("code")));
    }

    [DatabaseFact]
    public async Task Source_by_number_list_and_detail()
    {
        var cashier = await NewCashierAsync();
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, [(product, 2m)], [Pay("CASH", 10000m)]);

        var (status, source) = await _client.GetAsync($"/api/v1/returns/source?number={sale.Str("number")}", cashier.Token);
        var (unknown, unknownBody) = await _client.GetAsync("/api/v1/returns/source?number=TK01-SL-000000-999999", cashier.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(source.GetProperty("within_window").GetBoolean());
        Assert.Equal(BranchClock.Today(DateTime.UtcNow, "Asia/Jakarta").ToString("yyyy-MM-dd"), source.Str("sale_date"));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_sale"), (unknown, unknownBody.Str("code")));

        var lineId = source.GetProperty("lines")[0].Id("sale_line_id");
        var (_, created) = await ReturnAsync(cashier, device, sale.Id(), "REFUND", [(lineId, 1m, "GOOD")]);
        var (_, list) = await _client.GetAsync("/api/v1/returns", cashier.Token);
        var (_, detail) = await _client.GetAsync($"/api/v1/returns/{created.Id()}", cashier.Token);

        var row = list.EnumerateArray().Single(r => r.Id() == created.Id());
        Assert.Equal((sale.Str("number"), "REFUND", 5000m, "Asia/Jakarta"), (row.Str("sale_number"), row.Str("settlement"), row.Dec("total"), row.Str("time_zone")));
        Assert.Equal(("Barang rusak", "uji retur", "TOKO DEV"), (detail.Str("reason_name"), detail.Str("notes"), detail.GetProperty("receipt").Str("store_name")));
    }
}
