using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

/// <summary>Void penjualan (10b): open shift only, reason + notes, approval, stock back per expiry, idempotent.</summary>
[Collection(DatabaseCollection.Name)]
public class SaleVoidApiTests(DatabaseFixture db) : IAsyncLifetime
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

    private sealed record User(Guid Id, string Name, string Token);

    private async Task<User> NewUserAsync(params string[] permissions)
    {
        var name = $"sv{Interlocked.Increment(ref _users)}_{Guid.NewGuid():N}"[..30];
        var id = await db.CreateUserAsync(name, "secret-1", roles: (JsonSerializer.Serialize(permissions), db.NodeId));
        return new User(id, name, db.TokenFor(id, name, permissions));
    }

    private async Task<(Guid Device, Guid Shift)> OpenShiftAsync(User cashier)
    {
        var device = await db.CreateDeviceAsync();
        var (status, body) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 100000m }, cashier.Token, device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (device, body.Id());
    }

    private async Task<JsonElement> SellAsync(User cashier, Guid device, TestProduct product, decimal qty, object[] payments, Guid? customer = null)
    {
        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            Lines = new[] { new { product.ProductUnitId, Qty = qty } },
            Payments = payments,
        }, cashier.Token, device);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return body.GetProperty("sale");
    }

    private static object Pay(string method, decimal amount) => new { Method = method, Amount = amount };

    private async Task<Guid> ReasonAsync(string type = "SALE_VOID")
    {
        var (_, reasons) = await _client.GetAsync($"/api/v1/reasons?type={type}", (await NewUserAsync("sale.create")).Token);
        return reasons.EnumerateArray().First().Id();
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Void(User user, Guid saleId, Guid? id = null, Guid? reason = null,
        string? notes = "salah input", string? approvalToken = null, Guid? reasonId = null) =>
        VoidCoreAsync(user, saleId, id ?? Guid.CreateVersion7(), reasonId ?? reason, notes, approvalToken);

    private async Task<(HttpStatusCode Status, JsonElement Body)> VoidCoreAsync(User user, Guid saleId, Guid id, Guid? reason, string? notes, string? token) =>
        await _client.PostAsync($"/api/v1/sales/{saleId}/void",
            new { Id = id, ReasonId = reason ?? await ReasonAsync(), Notes = notes, ApprovalToken = token }, user.Token);

    private async Task<string> ApproveAsync(User cashier, User supervisor)
    {
        var (status, body) = await _client.PostAsync("/api/v1/auth/approve",
            new { Action = "sale.void", Username = supervisor.Name, Password = "secret-1" }, cashier.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.Str("approval_token");
    }

    private async Task<Dictionary<DateOnly, decimal>> BalancesAsync(Guid product)
    {
        await using var connection = await db.Factory.OpenAsync();
        var rows = await connection.QueryAsync<(DateTime Key, decimal Qty)>(
            "SELECT expiry_key, base_qty FROM stock_balances WHERE product_id = @product AND location_id = @LocationA",
            new { product, db.LocationA });
        return rows.Where(r => r.Key.Year != 9999 || r.Qty != 0).ToDictionary(r => DateOnly.FromDateTime(r.Key), r => r.Qty);  // dated buckets
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    [DatabaseFact]
    public async Task Void_with_supervisor_approval_puts_stock_back_per_expiry_and_cancels_the_receivable()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var supervisor = await NewUserAsync("sale.void");
        var (device, shift) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(10000m, trackExpiry: true, stock: [(E1, 3m), (E2, 10m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m);
        var sale = await SellAsync(cashier, device, product, 5m, [Pay("CASH", 10000m), Pay("CREDIT", 40000m)], customer);  // E1 3 + E2 2
        Assert.Equal(new Dictionary<DateOnly, decimal> { [E1] = 0m, [E2] = 8m }, await BalancesAsync(product.ProductId));

        var (noApproval, noApprovalBody) = await Void(cashier, sale.Id());
        Assert.Equal((HttpStatusCode.Forbidden, "void_approval_required"), (noApproval, noApprovalBody.Str("code")));

        var id = Guid.CreateVersion7();
        var (status, body) = await Void(cashier, sale.Id(), id, approvalToken: await ApproveAsync(cashier, supervisor));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal((id, sale.Str("number"), "salah input"), (body.Id(), body.Str("sale_number"), body.Str("notes")));
        Assert.Equal(supervisor.Id, body.Id("approved_by"));
        Assert.Equal(new Dictionary<DateOnly, decimal> { [E1] = 3m, [E2] = 10m }, await BalancesAsync(product.ProductId));
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_ledger WHERE doc_type = 'SALE_VOID' AND doc_id = @id AND base_qty > 0", new { id }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_type = @type AND doc_id = @id", new { type = SyncDocTypes.SaleVoid, id }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE id = @id", new { id = sale.Id() }));  // the sale stays

        // the receivable no longer counts and Data Penjualan says Void
        var (_, customers) = await _client.GetAsync($"/api/v1/customers/search?q={await ScalarAsync<string>("SELECT code FROM contacts WHERE id = @customer", new { customer })}", cashier.Token);
        Assert.Equal(1000000m, customers[0].Dec("credit_remaining"));
        var (_, list) = await _client.GetAsync($"/api/v1/sales?q={sale.Str("number")}", cashier.Token);
        var row = list.GetProperty("items")[0];
        Assert.Equal(("VOID", 0m), (row.Str("status"), row.Dec("outstanding")));

        // Kartu Stok: "Void Penjualan" with the sale's number
        var (_, card) = await _client.GetAsync($"/api/v1/stock/card?product_id={product.ProductId}&location_id={db.LocationA}",
            db.TokenFor((await NewUserAsync("stock.view")).Id, "viewer", ["stock.view"]));
        Assert.Contains(card.GetProperty("rows").EnumerateArray(),
            r => r.Str("doc_label") == "Void Penjualan" && r.Str("doc_number") == sale.Str("number") && r.Dec("qty_in") == 3m);

        // Tutup Shift: the voided sale is no nota, no cash, one cancellation
        var (_, closed) = await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 100000m }, cashier.Token, device);
        Assert.Equal((100000m, 0, 1), (closed.Dec("expected_cash"), closed.GetProperty("sale_count").GetInt32(), closed.GetProperty("cancellation_count").GetInt32()));
    }

    [DatabaseFact]
    public async Task With_sale_void_no_approval_is_needed_and_points_are_cancelled()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: null);
        var sale = await SellAsync(cashier, device, product, 2m, [Pay("CASH", 10000m)], customer);
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO loyalty_point_ledger (id, node_id, customer_id, points, doc_type, doc_id, occurred_at) VALUES (@id, @NodeId, @customer, 10, 'SALE', @sale, UTC_TIMESTAMP(3))",
                new { id = Guid.CreateVersion7(), db.NodeId, customer, sale = sale.Id() });
        }

        var (status, body) = await Void(cashier, sale.Id());

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("approved_by").ValueKind);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COALESCE(SUM(points), 0) FROM loyalty_point_ledger WHERE customer_id = @customer", new { customer }));
    }

    [DatabaseFact]
    public async Task A_sale_of_a_closed_shift_cannot_be_voided()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, shift) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);
        await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = 105000m }, cashier.Token, device);

        var (status, body) = await Void(cashier, sale.Id());

        Assert.Equal((HttpStatusCode.Conflict, "shift_closed"), (status, body.Str("code")));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sale_voids WHERE sale_id = @id", new { id = sale.Id() }));
    }

    [DatabaseFact]
    public async Task One_void_per_sale_and_a_retry_returns_the_stored_void()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);
        var id = Guid.CreateVersion7();
        var reason = await ReasonAsync();

        var first = await Void(cashier, sale.Id(), id, reason);
        var retry = await Void(cashier, sale.Id(), id, reason);
        var other = await Void(cashier, sale.Id(), Guid.CreateVersion7(), reason);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(first.Body.Str("voided_at"), retry.Body.Str("voided_at"));
        Assert.Equal((HttpStatusCode.Conflict, "already_voided"), (other.Status, other.Body.Str("code")));
        Assert.Equal(10m, await ScalarAsync<decimal>(
            "SELECT SUM(base_qty) FROM stock_balances WHERE product_id = @ProductId", new { product.ProductId }));   // back once
    }

    [DatabaseFact]
    public async Task Parallel_voids_store_one()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, product, 2m, [Pay("CASH", 10000m)]);
        var id = Guid.CreateVersion7();
        var reason = await ReasonAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Void(cashier, sale.Id(), id, reason)));

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body.ToString()));
        Assert.Equal(10m, await ScalarAsync<decimal>(
            "SELECT SUM(base_qty) FROM stock_balances WHERE product_id = @ProductId", new { product.ProductId }));
    }

    [DatabaseFact]
    public async Task Notes_and_a_void_reason_are_required()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create", "sale.void");
        var (device, _) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 10m)]);
        var sale = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);

        var noNotes = await Void(cashier, sale.Id(), notes: " ");
        var wrongReason = await Void(cashier, sale.Id(), reasonId: await ReasonAsync("RETURN"));
        var unknownSale = await Void(cashier, Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "reason_required"), (noNotes.Status, noNotes.Body.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "unknown_reason"), (wrongReason.Status, wrongReason.Body.Str("code")));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_sale"), (unknownSale.Status, unknownSale.Body.Str("code")));
    }

    [DatabaseFact]
    public async Task Reasons_are_listed_by_type()
    {
        var user = await NewUserAsync("sale.create");

        var (_, voids) = await _client.GetAsync("/api/v1/reasons?type=SALE_VOID", user.Token);
        var (_, returns) = await _client.GetAsync("/api/v1/reasons?type=RETURN", user.Token);

        Assert.Contains("Salah input", voids.EnumerateArray().Select(r => r.Str("name")));
        Assert.All(voids.EnumerateArray(), r => Assert.Equal("SALE_VOID", r.Str("type")));
        Assert.Contains("Barang rusak", returns.EnumerateArray().Select(r => r.Str("name")));
    }
}
