using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.NodeService.Sales;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class SalesApiTests(DatabaseFixture db) : IAsyncLifetime
{
    private static readonly DateOnly E1 = new(2030, 1, 1);
    private static readonly DateOnly E2 = new(2030, 6, 1);
    private static readonly DateOnly Expired = new(2020, 1, 1);

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

    private async Task<(Guid Device, Guid Shift)> OpenShiftAsync(decimal openingCash = 100000m)
    {
        var device = await db.CreateDeviceAsync();
        var (status, body) = await _client.PostAsync("/api/v1/shifts/open",
            new { CashierId = db.Seed.CashierId, DeviceId = device, OpeningCash = openingCash });
        Assert.Equal(HttpStatusCode.Created, status);
        return (device, body.Id());
    }

    private static object Line(TestProduct p, decimal qty, decimal discountPct = 0m, decimal discountAmount = 0m) =>
        new { ProductUnitId = p.ProductUnitId, Qty = qty, DiscountPct = discountPct, DiscountAmount = discountAmount };

    private static object Pay(string method, decimal amount) => new { Method = method, Amount = amount };

    private Task<(HttpStatusCode Status, JsonElement Body)> Sell(Guid device, object[] lines, object[] payments,
        Guid? id = null, Guid? customer = null) =>
        _client.PostAsync("/api/v1/sales", new
        {
            Id = id ?? Guid.CreateVersion7(),
            DeviceId = device,
            CustomerId = customer,
            Lines = lines,
            Payments = payments,
        });

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<Dictionary<DateOnly, decimal>> BalancesAsync(Guid product)
    {
        await using var connection = await db.Factory.OpenAsync();
        var rows = await connection.QueryAsync<(DateTime Key, decimal Qty)>(
            "SELECT expiry_key, base_qty FROM stock_balances WHERE product_id = @product AND location_id = @LocationA",
            new { product, db.LocationA });
        return rows.ToDictionary(r => DateOnly.FromDateTime(r.Key), r => r.Qty);
    }

    // ---- tests --------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Normal_cash_sale_is_saved_with_rounding_change_and_outbox()
    {
        var (device, shift) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(4560m, stock: [(null, 10m)]);

        // 3 x 4560 = 13680 -> 13700 (nearest 100); pay 20000 -> change 6300
        var (status, body) = await Sell(device, [Line(product, 3m)], [Pay("CASH", 20000m)]);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
        var sale = body.GetProperty("sale");
        Assert.Matches(@"^TK01-SL-\d{6}-\d{6}$", sale.Str("number"));
        Assert.Equal(shift, sale.Id("shift_id"));
        Assert.Equal(13680m, sale.Dec("subtotal"));
        Assert.Equal(20m, sale.Dec("rounding_amount"));
        Assert.Equal(13700m, sale.Dec("total"));
        Assert.Equal(0m, sale.Dec("tax_amount"));
        Assert.Equal(6300m, sale.Dec("change"));

        var line = sale.GetProperty("lines")[0];
        Assert.Equal(4560m, line.Dec("unit_price"));
        Assert.Equal(3m, line.Dec("base_qty"));
        Assert.Equal(13680m, line.Dec("line_total"));

        var payment = sale.GetProperty("payments")[0];
        Assert.Equal("CASH", payment.Str("method"));
        Assert.Equal(13700m, payment.Dec("amount"));
        Assert.Equal(20000m, payment.Dec("tendered"));
        Assert.Equal(6300m, payment.Dec("change_given"));

        Assert.Equal(7m, (await BalancesAsync(product.ProductId))[new DateOnly(9999, 12, 31)]);

        var payload = await ScalarAsync<string>("SELECT payload FROM sync_outbox WHERE doc_type = @type AND doc_id = @id",
            new { type = SyncDocTypes.Sale, id = sale.Id() });
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(sale.Str("number"), json.RootElement.Str("number"));
        Assert.Equal("13700.0000", json.RootElement.Str("total"));
        Assert.Single(json.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Single(json.RootElement.GetProperty("payments").EnumerateArray());
    }

    [DatabaseFact]
    public async Task Stock_is_taken_fefo_per_expiry_and_skips_expired()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(3000m, trackExpiry: true,
            stock: [(Expired, 3m), (E2, 5m), (E1, 2m)]);

        var (status, body) = await Sell(device, [Line(product, 4m)], [Pay("CASH", 12000m)]);

        Assert.Equal(HttpStatusCode.Created, status);
        var allocations = body.GetProperty("sale").GetProperty("lines")[0].GetProperty("stock").EnumerateArray()
            .Select(a => (a.Str("expiry_date"), a.Dec("base_qty"))).ToList();
        Assert.Equal([("2030-01-01", 2m), ("2030-06-01", 2m)], allocations);

        var balances = await BalancesAsync(product.ProductId);
        Assert.Equal(3m, balances[Expired]);
        Assert.Equal(0m, balances[E1]);
        Assert.Equal(3m, balances[E2]);

        var lineId = body.GetProperty("sale").GetProperty("lines")[0].Id();
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_ledger WHERE doc_type = 'SALE' AND doc_line_id = @lineId", new { lineId }));
    }

    [DatabaseFact]
    public async Task Barcode_line_uses_wholesale_price_and_manual_discount()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(4000m, tiers: [(12m, 3500m)]);

        // 12 x 3500 = 42000, 10% = 4200 -> 37800
        var (status, body) = await Sell(device,
            [new { Barcode = product.Barcode, Qty = 12m, DiscountPct = 10m }],
            [Pay("QRIS", 37800m)]);

        Assert.Equal(HttpStatusCode.Created, status);
        var line = body.GetProperty("sale").GetProperty("lines")[0];
        Assert.Equal(3500m, line.Dec("unit_price"));
        Assert.Equal(4200m, line.Dec("discount_amount"));
        Assert.Equal(37800m, line.Dec("line_total"));
        Assert.Equal(0m, body.GetProperty("sale").Dec("rounding_amount"));   // no cash, no rounding
    }

    [DatabaseFact]
    public async Task Same_id_sent_again_returns_the_stored_sale()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var id = Guid.CreateVersion7();

        var (s1, first) = await Sell(device, [Line(product, 2m)], [Pay("CASH", 10000m)], id);
        var (s2, second) = await Sell(device, [Line(product, 2m)], [Pay("CASH", 10000m)], id);

        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.True(second.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.GetProperty("sale").Str("number"), second.GetProperty("sale").Str("number"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE id = @id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_id = @id", new { id }));
        Assert.Equal(8m, (await BalancesAsync(product.ProductId))[new DateOnly(9999, 12, 31)]);
    }

    [DatabaseFact]
    public async Task Same_id_sent_in_parallel_creates_one_sale()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var id = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Sell(device, [Line(product, 1m)], [Pay("CASH", 5000m)], id)));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body.ToString()));
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.Single(results.Select(r => r.Body.GetProperty("sale").Str("number")).Distinct());
        Assert.Equal(9m, (await BalancesAsync(product.ProductId))[new DateOnly(9999, 12, 31)]);
    }

    [DatabaseFact]
    public async Task Sale_without_open_shift_is_rejected()
    {
        var device = await db.CreateDeviceAsync();
        var product = await db.CreateSellableProductAsync(5000m);

        var (status, body) = await Sell(device, [Line(product, 1m)], [Pay("CASH", 5000m)]);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_open_shift", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Insufficient_payment_is_rejected_and_nothing_is_written()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var id = Guid.CreateVersion7();

        var (status, body) = await Sell(device, [Line(product, 2m)], [Pay("CASH", 9000m)], id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("insufficient_payment", body.Str("code"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE id = @id", new { id }));
        Assert.Equal(10m, (await BalancesAsync(product.ProductId))[new DateOnly(9999, 12, 31)]);
    }

    [DatabaseFact]
    public async Task Invalid_lines_are_rejected()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(5000m);

        var unknown = await Sell(device, [new { ProductUnitId = Guid.CreateVersion7(), Qty = 1m }], [Pay("CASH", 5000m)]);
        var badBarcode = await Sell(device, [new { Barcode = "NOPE", Qty = 1m }], [Pay("CASH", 5000m)]);
        var fraction = await Sell(device, [Line(product, 1.5m)], [Pay("CASH", 10000m)]);
        var badMethod = await Sell(device, [Line(product, 1m)], [Pay("BITCOIN", 5000m)]);
        var badId = await Sell(device, [Line(product, 1m)], [Pay("CASH", 5000m)], id: Guid.NewGuid());

        Assert.Equal("unknown_product_unit", unknown.Body.Str("code"));
        Assert.Equal("unknown_barcode", badBarcode.Body.Str("code"));
        Assert.Equal("invalid_quantity", fraction.Body.Str("code"));
        Assert.Equal("unknown_payment_method", badMethod.Body.Str("code"));
        Assert.Equal("invalid_id", badId.Body.Str("code"));
        Assert.All(new[] { unknown, badBarcode, fraction, badMethod, badId },
            r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status));
    }

    [DatabaseFact]
    public async Task Credit_sale_records_receivable_and_due_date()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(25000m, stock: [(null, 10m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 500000m, paymentTermDays: 14);

        // total 50000: cash 20000 (rounding applies, already a multiple of 100), credit 30000
        var (status, body) = await Sell(device, [Line(product, 2m)], [Pay("CASH", 20000m), Pay("CREDIT", 30000m)], customer: customer);

        Assert.Equal(HttpStatusCode.Created, status);
        var sale = body.GetProperty("sale");
        Assert.Equal(30000m, sale.Dec("credit_amount"));
        Assert.Equal(customer, sale.Id("customer_id"));
        Assert.False(string.IsNullOrEmpty(sale.Str("due_date")));
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
    }

    [DatabaseFact]
    public async Task Credit_over_limit_is_allowed_with_warning()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 10m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 15000m);

        await Sell(device, [Line(product, 1m)], [Pay("CREDIT", 10000m)], customer: customer);
        var (status, body) = await Sell(device, [Line(product, 1m)], [Pay("CREDIT", 10000m)], customer: customer);

        Assert.Equal(HttpStatusCode.Created, status);
        var warning = Assert.Single(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal("credit_limit_exceeded", warning.Str("code"));
    }

    [DatabaseFact]
    public async Task Credit_rules_customer_required_and_facility_required()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(10000m);
        var noFacility = await db.CreateCustomerAsync(creditLimit: null);

        var withoutCustomer = await Sell(device, [Line(product, 1m)], [Pay("CREDIT", 10000m)]);
        var withoutFacility = await Sell(device, [Line(product, 1m)], [Pay("CREDIT", 10000m)], customer: noFacility);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "credit_requires_customer"), (withoutCustomer.Status, withoutCustomer.Body.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "credit_not_allowed"), (withoutFacility.Status, withoutFacility.Body.Str("code")));
    }

    [DatabaseFact]
    public async Task Shift_close_counts_cash_sales()
    {
        var (device, shift) = await OpenShiftAsync(openingCash: 100000m);
        var product = await db.CreateSellableProductAsync(4560m, stock: [(null, 10m)]);

        await Sell(device, [Line(product, 3m)], [Pay("CASH", 20000m)]);    // total 13700, change 6300
        await Sell(device, [Line(product, 1m)], [Pay("QRIS", 4560m)]);     // not cash

        var (_, closed) = await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 113700m });

        Assert.Equal(113700m, closed.Dec("expected_cash"));
        Assert.Equal(0m, closed.Dec("difference"));
    }

    [DatabaseFact]
    public async Task Failure_in_any_step_rolls_back_everything()
    {
        var (device, _) = await OpenShiftAsync();
        var product = await db.CreateSellableProductAsync(5000m, trackExpiry: true, stock: [(E1, 10m)]);
        var period = Pos.Shared.Documents.DocumentNumber.Period(DateTime.UtcNow, "Asia/Jakarta");
        var lastBefore = await ScalarAsync<int?>(
            "SELECT last_number FROM document_sequences WHERE doc_type = 'SL' AND period = @period", new { period }) ?? 0;

        var failing = new SaleService(new TransactionRunner(db.Factory), new NodeIdentity(db.Factory, "TK01"), TimeProvider.System,
            new SaleHooks { AfterStep = (step, _) => step == SaleSteps.OutboxWritten ? throw new InvalidOperationException("boom") : Task.CompletedTask });
        var id = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CreateAsync(new CreateSaleRequest(
            id, device, null, null, null,
            [new SaleLineRequest(product.ProductUnitId, null, 2m)],
            [new SalePaymentRequest("CASH", 10000m)])));

        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE id = @id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sale_lines WHERE sale_id = @id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sale_payments WHERE sale_id = @id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM stock_ledger WHERE doc_id = @id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_id = @id", new { id }));
        Assert.Equal(10m, (await BalancesAsync(product.ProductId))[E1]);
        Assert.Equal(lastBefore, await ScalarAsync<int?>(
            "SELECT last_number FROM document_sequences WHERE doc_type = 'SL' AND period = @period", new { period }) ?? 0);
    }

    [DatabaseFact]
    public async Task Ten_parallel_sales_on_several_devices_get_unique_gapless_numbers()
    {
        var shifts = new List<Guid>();
        for (var i = 0; i < 4; i++)
            shifts.Add((await OpenShiftAsync()).Device);
        var product = await db.CreateSellableProductAsync(2500m, trackExpiry: true, stock: [(E1, 6m), (E2, 6m)]);
        var period = Pos.Shared.Documents.DocumentNumber.Period(DateTime.UtcNow, "Asia/Jakarta");
        var lastBefore = await ScalarAsync<int?>(
            "SELECT last_number FROM document_sequences WHERE doc_type = 'SL' AND period = @period", new { period }) ?? 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => Sell(shifts[i % shifts.Count], [Line(product, 1m)], [Pay("CASH", 2500m)]))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var suffixes = results.Select(r => int.Parse(r.Body.GetProperty("sale").Str("number")[^6..])).Order().ToList();
        Assert.Equal(Enumerable.Range(lastBefore + 1, 10), suffixes);

        var balances = await BalancesAsync(product.ProductId);
        Assert.Equal(0m, balances[E1]);
        Assert.Equal(2m, balances[E2]);
    }
}
