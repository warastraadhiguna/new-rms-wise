using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

/// <summary>Tutup Shift (9d): summary figures, blind count, closing another cashier's shift, idempotent retry.</summary>
[Collection(DatabaseCollection.Name)]
public class ShiftCloseApiTests(DatabaseFixture db) : IAsyncLifetime
{
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

    /// <summary>A user of its own for this test (never the seeded ones), with a token for exactly these permissions.</summary>
    private async Task<User> NewUserAsync(params string[] permissions)
    {
        var name = $"sc{Interlocked.Increment(ref _users)}_{Guid.NewGuid():N}"[..30];
        var json = JsonSerializer.Serialize(permissions);
        var id = await db.CreateUserAsync(name, "secret-1", roles: (json, db.NodeId));
        return new User(id, name, db.TokenFor(id, name, permissions));
    }

    private Task<User> NewCashierAsync() => NewUserAsync("shift.open", "sale.create");

    private async Task<(Guid Device, Guid Shift)> OpenShiftAsync(User cashier, decimal openingCash = 200000m)
    {
        var device = await db.CreateDeviceAsync();
        var (status, body) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = openingCash }, cashier.Token, device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (device, body.Id());
    }

    private async Task<Guid> SellAsync(User cashier, Guid device, TestProduct product, decimal qty, object[] payments, Guid? customer = null)
    {
        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            Lines = new[] { new { product.ProductUnitId, Qty = qty, DiscountPct = 0m, DiscountAmount = 0m } },
            Payments = payments,
        }, cashier.Token, device);
        Assert.True(status is HttpStatusCode.Created, $"{status}: {body}");
        return body.GetProperty("sale").Id();
    }

    private static object Pay(string method, decimal amount) => new { Method = method, Amount = amount };

    private Task<(HttpStatusCode Status, JsonElement Body)> Close(User user, Guid device, Guid shift, decimal counted, string? notes = null) =>
        _client.PostAsync("/api/v1/shifts/close", new { ShiftId = shift, CountedCash = counted, Notes = notes }, user.Token, device);

    private Task<(HttpStatusCode Status, JsonElement Body)> Closing(User user, Guid device, Guid shift) =>
        _client.GetAsync($"/api/v1/shifts/{shift}/closing", user.Token, device);

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private static Dictionary<string, (decimal Amount, int Count)> NonCash(JsonElement body) =>
        body.GetProperty("non_cash").EnumerateArray().ToDictionary(
            m => m.Str("code"), m => (m.Dec("amount"), m.GetProperty("count").GetInt32()));

    // ---- tests --------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Summary_matches_the_shifts_transactions()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 200000m);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 100m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 10000000m);

        await SellAsync(cashier, device, product, 3m, [Pay("CASH", 50000m)]);             // cash 30.000 (change 20.000)
        await SellAsync(cashier, device, product, 1m, [Pay("CASH", 10000m)]);             // cash 10.000
        await SellAsync(cashier, device, product, 2m, [Pay("DEBIT", 20000m)]);
        await SellAsync(cashier, device, product, 4m, [Pay("QRIS", 40000m)]);
        await SellAsync(cashier, device, product, 5m, [Pay("TRANSFER", 50000m)]);
        await SellAsync(cashier, device, product, 6m, [Pay("CASH", 15000m), Pay("CREDIT", 45000m)], customer); // DP 15.000
        await SellAsync(cashier, device, product, 1m, [Pay("CREDIT", 10000m)], customer);
        var voided = await SellAsync(cashier, device, product, 7m, [Pay("CASH", 70000m)]);

        // documents without an API yet: a void of the last sale, a receivable payment in cash, a cash expense,
        // and one cancelled cart through the API
        var reason = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO reasons (id, type, name) VALUES (@reason, 'SALE_VOID', 'Salah input')", new { reason });
        await ExecAsync(
            "INSERT INTO sale_voids (id, node_id, sale_id, reason_id, voided_at, created_by) VALUES (@id, @NodeId, @voided, @reason, UTC_TIMESTAMP(3), @user)",
            new { id = Guid.CreateVersion7(), db.NodeId, voided, reason, user = cashier.Id });
        await ExecAsync(
            """
            INSERT INTO receivable_payments (id, node_id, number, customer_id, shift_id, paid_at, payment_method_id, amount, created_by)
            SELECT @id, @NodeId, @number, @customer, @shift, UTC_TIMESTAMP(3), id, 25000, @user FROM payment_methods WHERE code = 'CASH'
            """,
            new { id = Guid.CreateVersion7(), db.NodeId, number = "TK01-RP-" + Guid.NewGuid().ToString("N")[..12], customer, shift, user = cashier.Id });
        var expenseType = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO expense_types (id, name) VALUES (@expenseType, 'Parkir')", new { expenseType });
        await ExecAsync(
            """
            INSERT INTO cash_expenses (id, node_id, number, expense_type_id, shift_id, spent_at, amount, created_by)
            VALUES (@id, @NodeId, @number, @expenseType, @shift, UTC_TIMESTAMP(3), 12000, @user)
            """,
            new { id = Guid.CreateVersion7(), db.NodeId, number = "TK01-EX-" + Guid.NewGuid().ToString("N")[..12], expenseType, shift, user = cashier.Id });
        var (cancelStatus, _) = await _client.PostAsync("/api/v1/cart-cancellations", new
        {
            Id = Guid.CreateVersion7(),
            Reason = "Pembeli batal",
            TotalAmount = 10000m,
            Lines = new[] { new { product.ProductUnitId, Qty = 1m, UnitPrice = 10000m, LineTotal = 10000m } },
        }, cashier.Token, device);
        Assert.Equal(HttpStatusCode.Created, cancelStatus);

        // expected = 200.000 + 40.000 cash + 15.000 DP + 25.000 receivable - 12.000 expense = 268.000
        var (status, closed) = await Close(cashier, device, shift, counted: 267500m);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("CLOSED", closed.Str("status"));
        Assert.Equal(200000m, closed.Dec("opening_cash"));
        Assert.Equal(40000m, closed.Dec("cash_sales"));
        Assert.Equal(15000m, closed.Dec("credit_down_payments"));
        Assert.Equal(25000m, closed.Dec("receivable_cash"));
        Assert.Equal(12000m, closed.Dec("cash_expenses"));
        Assert.Equal(268000m, closed.Dec("expected_cash"));
        Assert.Equal(267500m, closed.Dec("counted_cash"));
        Assert.Equal(-500m, closed.Dec("difference"));
        Assert.Equal(7, closed.GetProperty("sale_count").GetInt32());              // the voided sale is not a nota
        Assert.Equal(2, closed.GetProperty("cancellation_count").GetInt32());      // one Batal + one void
        Assert.Equal(["DEBIT", "QRIS", "TRANSFER", "CREDIT"], NonCash(closed).Keys);
        Assert.Equal((20000m, 1), NonCash(closed)["DEBIT"]);
        Assert.Equal((40000m, 1), NonCash(closed)["QRIS"]);
        Assert.Equal((50000m, 1), NonCash(closed)["TRANSFER"]);
        Assert.Equal((55000m, 2), NonCash(closed)["CREDIT"]);
        Assert.Equal(cashier.Id, closed.Id("closed_by_id"));
        Assert.Equal("Asia/Jakarta", closed.Str("time_zone"));
        Assert.Equal("TOKO DEV", closed.GetProperty("receipt").Str("store_name"));

        // frozen on the row and in the outbox (row_version 2)
        Assert.Equal(268000m, await ScalarAsync<decimal>("SELECT expected_cash FROM cashier_shifts WHERE id = @shift", new { shift }));
        Assert.Equal(7, await ScalarAsync<int>("SELECT sale_count FROM cashier_shifts WHERE id = @shift", new { shift }));
        var payload = await ScalarAsync<string>(
            "SELECT CAST(payload AS CHAR) FROM sync_outbox WHERE doc_type = @type AND doc_id = @shift AND row_version = 2",
            new { type = SyncDocTypes.CashierShift, shift });
        using var outbox = JsonDocument.Parse(payload);
        var tables = outbox.RootElement.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(["devices", "cashier_shifts"], tables.Select(t => t.Str("table")));
        var stored = tables[1].GetProperty("rows")[0];
        Assert.Equal(40000m, stored.Dec("cash_sales"));
        Assert.Equal("CLOSED", stored.Str("status"));
        Assert.Equal(2, stored.GetProperty("row_version").GetInt32());
    }

    [DatabaseFact]
    public async Task Blind_count_hides_the_figures_until_the_counted_cash_is_saved()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 100000m);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 10m)]);
        await SellAsync(cashier, device, product, 2m, [Pay("CASH", 20000m)]);

        var (status, blind) = await Closing(cashier, device, shift);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(blind.GetProperty("blind_count").GetBoolean());               // dev seed: shift.blind_count = true
        Assert.Equal("OPEN", blind.Str("status"));
        Assert.Equal(100000m, blind.Dec("opening_cash"));
        Assert.Equal("Test user", blind.Str("cashier_name"));
        foreach (var hidden in new[] { "cash_sales", "expected_cash", "non_cash", "sale_count", "difference" })
            Assert.Equal(JsonValueKind.Null, blind.GetProperty(hidden).ValueKind);

        // the branch turns blind count off: the running summary is shown before counting
        var setting = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@setting, @BranchId, 'shift.blind_count', 'false')",
            new { setting, db.BranchId });
        try
        {
            var (_, open) = await Closing(cashier, device, shift);
            Assert.False(open.GetProperty("blind_count").GetBoolean());
            Assert.Equal(20000m, open.Dec("cash_sales"));
            Assert.Equal(120000m, open.Dec("expected_cash"));
            Assert.Equal(JsonValueKind.Null, open.GetProperty("counted_cash").ValueKind);
        }
        finally
        {
            await ExecAsync("DELETE FROM settings WHERE id = @setting", new { setting });
        }

        var (_, closed) = await Close(cashier, device, shift, counted: 120000m);
        Assert.Equal(120000m, closed.Dec("expected_cash"));
        Assert.Equal(0m, closed.Dec("difference"));

        var (_, afterwards) = await Closing(cashier, device, shift);             // a closed shift shows its frozen figures
        Assert.Equal(120000m, afterwards.Dec("expected_cash"));
        Assert.Equal("CLOSED", afterwards.Str("status"));
    }

    [DatabaseFact]
    public async Task Another_cashiers_shift_needs_shift_close_and_notes()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier);
        var otherCashier = await NewCashierAsync();
        var supervisor = await NewUserAsync("shift.close", "stock.view");

        var (getStatus, getBody) = await Closing(otherCashier, device, shift);
        var (closeStatus, closeBody) = await Close(otherCashier, device, shift, 200000m, notes: "tolong tutup");
        Assert.Equal((HttpStatusCode.Forbidden, "shift_close_forbidden"), (getStatus, getBody.Str("code")));
        Assert.Equal((HttpStatusCode.Forbidden, "shift_close_forbidden"), (closeStatus, closeBody.Str("code")));

        var (noNotes, noNotesBody) = await Close(supervisor, device, shift, 200000m, notes: "  ");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "notes_required"), (noNotes, noNotesBody.Str("code")));
        Assert.Equal("OPEN", await ScalarAsync<string>("SELECT status FROM cashier_shifts WHERE id = @shift", new { shift }));

        Assert.Equal(HttpStatusCode.OK, (await Closing(supervisor, device, shift)).Status);
        var (status, closed) = await Close(supervisor, device, shift, 200000m, notes: "kasir pulang sakit");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(cashier.Id, closed.Id("cashier_id"));
        Assert.Equal(supervisor.Id, closed.Id("closed_by_id"));
        Assert.Equal("Test user", closed.Str("closed_by_name"));
        Assert.Equal("kasir pulang sakit", closed.Str("notes"));
    }

    [DatabaseFact]
    public async Task The_cashier_closes_their_own_shift_without_shift_close_and_notes_are_optional()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 50000m);

        var (status, closed) = await Close(cashier, device, shift, 50000m);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0m, closed.Dec("difference"));
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("notes").ValueKind);
    }

    [DatabaseFact]
    public async Task Closing_again_with_the_same_count_returns_the_stored_summary_once()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier, openingCash: 80000m);

        var (first, firstBody) = await Close(cashier, device, shift, 79000m);
        var (retry, retryBody) = await Close(cashier, device, shift, 79000m);
        var (different, differentBody) = await Close(cashier, device, shift, 80000m);
        var supervisor = await NewUserAsync("shift.close");
        var (byOther, byOtherBody) = await Close(supervisor, device, shift, 79000m, notes: "lagi");

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, retry);
        Assert.Equal(firstBody.Str("closed_at"), retryBody.Str("closed_at"));
        Assert.Equal(-1000m, retryBody.Dec("difference"));
        Assert.Equal((HttpStatusCode.Conflict, "shift_already_closed"), (different, differentBody.Str("code")));
        Assert.Equal((HttpStatusCode.Conflict, "shift_already_closed"), (byOther, byOtherBody.Str("code")));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_id = @shift AND row_version = 2", new { shift }));
        Assert.Equal(79000m, await ScalarAsync<decimal>("SELECT counted_cash FROM cashier_shifts WHERE id = @shift", new { shift }));
    }

    [DatabaseFact]
    public async Task Parallel_closes_store_one_close()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Close(cashier, device, shift, 200000m)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(results.Select(r => r.Body.Str("closed_at")).Distinct());
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_id = @shift AND row_version = 2", new { shift }));
    }

    [DatabaseFact]
    public async Task A_closed_shift_takes_no_more_sales()
    {
        var cashier = await NewCashierAsync();
        var (device, shift) = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 10m)]);
        await Close(cashier, device, shift, 200000m);

        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = 1m, DiscountPct = 0m, DiscountAmount = 0m } },
            Payments = new[] { Pay("CASH", 10000m) },
        }, cashier.Token, device);

        Assert.Equal((HttpStatusCode.Conflict, "no_open_shift"), (status, body.Str("code")));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE shift_id = @shift", new { shift }));
    }

    [DatabaseFact]
    public async Task Unknown_shift_is_404()
    {
        var cashier = await NewCashierAsync();
        var device = await db.CreateDeviceAsync();

        var (status, body) = await Close(cashier, device, Guid.CreateVersion7(), 0m);
        var (getStatus, _) = await Closing(cashier, device, Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.NotFound, "unknown_shift"), (status, body.Str("code")));
        Assert.Equal(HttpStatusCode.NotFound, getStatus);
    }
}
