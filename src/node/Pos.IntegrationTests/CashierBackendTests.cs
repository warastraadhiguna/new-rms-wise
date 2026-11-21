using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

/// <summary>Sale discount with supervisor approval, qty multiplier, searches, approvals, current shift.</summary>
[Collection(DatabaseCollection.Name)]
public class CashierBackendTests(DatabaseFixture db) : IAsyncLifetime
{
    private const string Cashier10 = """["shift.open","shift.close","sale.create","discount.max:10"]""";
    private const string Supervisor = """["sale.create","sale.discount.override","sale.line.delete"]""";

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

    private static string NewName() => "u" + Guid.NewGuid().ToString("N")[..12];

    private async Task<(Guid Id, string Name, string Token)> UserAsync(string roles, string? pin = null)
    {
        var name = NewName();
        var id = await db.CreateUserAsync(name, "secret-1", pin, roles: (roles, db.NodeId));
        var (status, body) = await _client.PostAsync("/api/v1/auth/login", new { Username = name, Password = "secret-1" });
        Assert.Equal(HttpStatusCode.OK, status);
        return (id, name, body.Str("access_token"));
    }

    private async Task<Guid> OpenShiftAsync(string token)
    {
        var device = await db.CreateDeviceAsync();
        var (status, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token, device);
        Assert.Equal(HttpStatusCode.Created, status);
        return device;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Sell(
        string token, Guid device, TestProduct product, decimal qty, decimal pay,
        decimal? discountPct = null, decimal? discountAmount = null,
        string? supervisorUsername = null, string? supervisorPassword = null, string? approvalToken = null, Guid? id = null) =>
        _client.PostAsync("/api/v1/sales", new
        {
            Id = id ?? Guid.CreateVersion7(),
            Lines = new[] { new { ProductUnitId = product.ProductUnitId, Qty = qty } },
            Payments = pay > 0 ? new[] { new { Method = "QRIS", Amount = pay } } : [],
            DiscountPct = discountPct,
            DiscountAmount = discountAmount,
            SupervisorUsername = supervisorUsername,
            SupervisorPassword = supervisorPassword,
            ApprovalToken = approvalToken,
        }, token, device);

    private Task<(HttpStatusCode Status, JsonElement Body)> Approve(string token, string action, string username, string password) =>
        _client.PostAsync("/api/v1/auth/approve", new { Action = action, Username = username, Password = password }, token);

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    // ---- sale discount ------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Percent_discount_within_the_cashiers_limit()
    {
        var cashier = await UserAsync(Cashier10);
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        // 2 x 10000 = 20000, 10% = 2000 -> 18000 (QRIS: no cash rounding)
        var (status, body) = await Sell(cashier.Token, device, product, 2m, 18000m, discountPct: 10m);

        Assert.Equal(HttpStatusCode.Created, status);
        var sale = body.GetProperty("sale");
        Assert.Equal(20000m, sale.Dec("subtotal"));
        Assert.Equal(2000m, sale.Dec("discount_amount"));
        Assert.Equal(18000m, sale.Dec("total"));
        Assert.Equal(JsonValueKind.Null, sale.GetProperty("discount_approved_by").ValueKind);
        Assert.Equal(2000m, await ScalarAsync<decimal>("SELECT discount_amount FROM sales WHERE id = @id", new { id = sale.Id() }));
    }

    [DatabaseFact]
    public async Task Nominal_discount_is_capped_at_the_subtotal_and_still_needs_approval_above_the_limit()
    {
        var cashier = await UserAsync("""["shift.open","sale.create","discount.max:100"]""");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(5000m);

        var (status, body) = await Sell(cashier.Token, device, product, 1m, 0m, discountAmount: 7000m);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(5000m, body.GetProperty("sale").Dec("discount_amount"));
        Assert.Equal(0m, body.GetProperty("sale").Dec("total"));
    }

    [DatabaseFact]
    public async Task Discount_above_the_limit_without_approval_is_refused_and_nothing_is_written()
    {
        var cashier = await UserAsync(Cashier10);
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);
        var id = Guid.CreateVersion7();

        var (status, body) = await Sell(cashier.Token, device, product, 1m, 8500m, discountPct: 15m, id: id);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("discount_approval_required", body.Str("code"));
        Assert.Equal(10m, decimal.Parse(body.GetProperty("max_percent").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE id = @id", new { id }));
    }

    [DatabaseFact]
    public async Task Cashier_without_discount_right_needs_approval_for_any_discount()
    {
        var cashier = await UserAsync("""["shift.open","sale.create"]""");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var (status, body) = await Sell(cashier.Token, device, product, 1m, 9900m, discountAmount: 100m);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("discount_approval_required", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Supervisor_password_in_the_request_approves_a_larger_discount()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var (status, body) = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m,
            supervisorUsername: supervisor.Name, supervisorPassword: "secret-1");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(supervisor.Id, body.GetProperty("sale").Id("discount_approved_by"));
        Assert.Equal(cashier.Id, body.GetProperty("sale").Id("created_by"));
    }

    [DatabaseFact]
    public async Task Wrong_supervisor_password_or_supervisor_without_right_is_refused()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");
        var colleague = await UserAsync(Cashier10, pin: "1111");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var wrongPassword = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, supervisorUsername: supervisor.Name, supervisorPassword: "wrong-pass");
        var noRight = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, supervisorUsername: colleague.Name, supervisorPassword: "secret-1");

        Assert.Equal((HttpStatusCode.Forbidden, "approval_rejected"), (wrongPassword.Status, wrongPassword.Body.Str("code")));
        Assert.Equal((HttpStatusCode.Forbidden, "approval_not_permitted"), (noRight.Status, noRight.Body.Str("code")));
    }

    [DatabaseFact]
    public async Task Approval_token_is_single_use_and_bound_to_the_requester()
    {
        var cashier = await UserAsync(Cashier10);
        var other = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");
        var device = await OpenShiftAsync(cashier.Token);
        var otherDevice = await OpenShiftAsync(other.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var (approveStatus, approval) = await Approve(cashier.Token, "sale.discount.override", supervisor.Name, "secret-1");
        Assert.Equal(HttpStatusCode.OK, approveStatus);
        var token = approval.Str("approval_token");
        Assert.Equal(supervisor.Id, approval.GetProperty("approved_by").Id());

        var stolen = await Sell(other.Token, otherDevice, product, 1m, 7500m, discountPct: 25m, approvalToken: token);
        var first = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, approvalToken: token);
        var reuse = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, approvalToken: token);

        Assert.Equal((HttpStatusCode.Forbidden, "invalid_approval"), (stolen.Status, stolen.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(supervisor.Id, first.Body.GetProperty("sale").Id("discount_approved_by"));
        Assert.Equal((HttpStatusCode.Forbidden, "invalid_approval"), (reuse.Status, reuse.Body.Str("code")));
    }

    [DatabaseFact]
    public async Task Expired_approval_token_or_token_for_another_action_is_refused()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var expiring = (await Approve(cashier.Token, "sale.discount.override", supervisor.Name, "secret-1")).Body.Str("approval_token");
        await using (var connection = await db.Factory.OpenAsync())
            await connection.ExecuteAsync("UPDATE approval_tokens SET expires_at = UTC_TIMESTAMP(3) - INTERVAL 1 SECOND WHERE requested_by = @Id", new { cashier.Id });
        var lineDelete = (await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "secret-1")).Body.Str("approval_token");

        var expired = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, approvalToken: expiring);
        var wrongAction = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m, approvalToken: lineDelete);

        Assert.Equal("invalid_approval", expired.Body.Str("code"));
        Assert.Equal("invalid_approval", wrongAction.Body.Str("code"));
    }

    [DatabaseFact]
    public async Task Approve_endpoint_validates_action_credentials_and_login()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");

        var badAction = await Approve(cashier.Token, "sale.void.everything", supervisor.Name, "secret-1");
        var badPassword = await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "wrong-pass");
        var anonymous = await _client.PostAsync("/api/v1/auth/approve", new { Action = "sale.line.delete", Username = supervisor.Name, Password = "secret-1" });
        var before = DateTime.UtcNow;
        var ok = await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "secret-1");
        var after = DateTime.UtcNow;

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_action"), (badAction.Status, badAction.Body.Str("code")));
        Assert.Equal((HttpStatusCode.Forbidden, "approval_rejected"), (badPassword.Status, badPassword.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("sale.line.delete", ok.Body.Str("action"));
        var expiresAt = DateTime.Parse(ok.Body.Str("expires_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(expiresAt, before.AddSeconds(-1), after.AddMinutes(5).AddSeconds(1));
    }

    [DatabaseFact]
    public async Task Supervisor_pin_is_no_longer_accepted_for_approvals()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor, pin: "9876");
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var viaApprove = await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "9876");
        var viaSale = await Sell(cashier.Token, device, product, 1m, 7500m, discountPct: 25m,
            supervisorUsername: supervisor.Name, supervisorPassword: "9876");
        var pinField = await _client.PostAsync("/api/v1/auth/approve",
            new { Action = "sale.line.delete", Username = supervisor.Name, Pin = "9876" }, cashier.Token);

        Assert.Equal("approval_rejected", viaApprove.Body.Str("code"));
        Assert.Equal("approval_rejected", viaSale.Body.Str("code"));
        Assert.Equal("approval_rejected", pinField.Body.Str("code"));
    }

    [DatabaseFact]
    public async Task Wrong_supervisor_passwords_lock_the_supervisor_like_a_login()
    {
        var cashier = await UserAsync(Cashier10);
        var supervisor = await UserAsync(Supervisor);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "wrong-pass")).Status);

        var locked = await Approve(cashier.Token, "sale.line.delete", supervisor.Name, "secret-1");
        var login = await _client.PostAsync("/api/v1/auth/login", new { Username = supervisor.Name, Password = "secret-1" });

        Assert.Equal((HttpStatusCode.TooManyRequests, "account_locked"), (locked.Status, locked.Body.Str("code")));
        Assert.Equal(HttpStatusCode.TooManyRequests, login.Status);
    }

    [DatabaseFact]
    public async Task Discount_percent_and_amount_together_are_rejected()
    {
        var cashier = await UserAsync(Cashier10);
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(10000m);

        var (status, body) = await Sell(cashier.Token, device, product, 1m, 9000m, discountPct: 5m, discountAmount: 500m);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_discount"), (status, body.Str("code")));
    }

    // ---- qty multiplier -----------------------------------------------------------------------

    [DatabaseFact]
    public async Task Product_without_qty_multiplier_must_be_sold_one_by_one()
    {
        var cashier = await UserAsync(Cashier10);
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(1000m, allowQtyMultiplier: false);

        var two = await Sell(cashier.Token, device, product, 2m, 2000m);
        var one = await Sell(cashier.Token, device, product, 1m, 1000m);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "qty_multiplier_not_allowed"), (two.Status, two.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Created, one.Status);
    }

    // ---- product search -----------------------------------------------------------------------

    [DatabaseFact]
    public async Task Product_search_by_barcode_sku_and_all_name_words()
    {
        var marker = "Zq" + Guid.NewGuid().ToString("N")[..6];
        var sku = "SKU-" + marker;
        var product = await db.CreateSellableProductAsync(4500m, name: $"Kopi Susu {marker} Gula Aren", sku: sku,
            stock: [(null, 7m)], storageStock: 100m, tiers: [(12m, 4000m)]);
        await db.CreateSellableProductAsync(3000m, name: $"Kopi Hitam {marker}");
        var token = db.CashierToken();

        var byBarcode = await _client.GetAsync($"/api/v1/products/search?q={product.Barcode}", token);
        var bySku = await _client.GetAsync($"/api/v1/products/search?q={sku}", token);
        var byWords = await _client.GetAsync($"/api/v1/products/search?q=aren%20{marker.ToLowerInvariant()}%20KOPI", token);
        var byOneWord = await _client.GetAsync($"/api/v1/products/search?q=kopi%20{marker}", token);

        var item = Assert.Single(byBarcode.Body.EnumerateArray());
        Assert.Equal(product.ProductUnitId, item.Id("product_unit_id"));
        Assert.Equal("barcode", item.Str("matched_by"));
        Assert.Equal(4500m, item.Dec("price"));
        Assert.Equal(7m, item.Dec("stock_base_qty"));      // DISPLAY only, STORAGE is not sellable
        Assert.Equal("PCS", item.Str("unit_code"));
        Assert.Equal([(1m, 4500m), (12m, 4000m)],
            item.GetProperty("price_tiers").EnumerateArray().Select(t => (t.Dec("min_qty"), t.Dec("price"))));

        Assert.Equal("sku", Assert.Single(bySku.Body.EnumerateArray()).Str("matched_by"));
        Assert.Equal(product.ProductUnitId, Assert.Single(byWords.Body.EnumerateArray()).Id("product_unit_id"));
        Assert.Equal(2, byOneWord.Body.GetArrayLength());
    }

    [DatabaseFact]
    public async Task Product_search_uses_this_branchs_price()
    {
        var product = await db.CreateSellableProductAsync(17500m);
        await using (var connection = await db.Factory.OpenAsync())
            await connection.ExecuteAsync(
                """
                INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by)
                VALUES (@id, @ProductUnitId, @BranchId, 18000, 1, '2026-01-01', @AdminId)
                """, new { id = Guid.CreateVersion7(), product.ProductUnitId, db.BranchId, db.Seed.AdminId });

        var (_, body) = await _client.GetAsync($"/api/v1/products/search?q={product.Barcode}", db.CashierToken());

        Assert.Equal(18000m, Assert.Single(body.EnumerateArray()).Dec("price"));
    }

    [DatabaseFact]
    public async Task Product_search_limits_and_escapes()
    {
        var marker = "Lm" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 55; i++)
            await db.CreateSellableProductAsync(1000m, name: $"Item {marker} {i:D2}");
        var token = db.CashierToken();

        var many = await _client.GetAsync($"/api/v1/products/search?q={marker}", token);
        var wildcard = await _client.GetAsync("/api/v1/products/search?q=%25", token);
        var none = await _client.GetAsync("/api/v1/products/search?q=" + Guid.NewGuid().ToString("N"), token);
        var empty = await _client.GetAsync("/api/v1/products/search?q=%20", token);
        var anonymous = await _client.GetAsync($"/api/v1/products/search?q={marker}", token: null);

        Assert.Equal(50, many.Body.GetArrayLength());
        Assert.Equal(0, wildcard.Body.GetArrayLength());
        Assert.Equal(0, none.Body.GetArrayLength());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "query_required"), (empty.Status, empty.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    // ---- customer search ----------------------------------------------------------------------

    [DatabaseFact]
    public async Task Customer_search_by_code_name_and_phone_with_credit_and_points()
    {
        var customer = await db.CreateCustomerAsync(creditLimit: 500000m);
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync("UPDATE contacts SET name = 'Siti Aminah Rahayu', phone = '081299887766' WHERE id = @customer", new { customer });
            await connection.ExecuteAsync(
                """
                INSERT INTO loyalty_point_ledger (id, node_id, customer_id, points, doc_type, doc_id, occurred_at) VALUES
                  (@a, @NodeId, @customer, 120, 'SALE', @a, UTC_TIMESTAMP(3)),
                  (@b, @NodeId, @customer, -20, 'SALE', @b, UTC_TIMESTAMP(3))
                """, new { a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), db.NodeId, customer });
        }
        var code = await ScalarAsync<string>("SELECT code FROM contacts WHERE id = @customer", new { customer });

        // 30000 on credit
        var cashier = await UserAsync(Cashier10);
        var device = await OpenShiftAsync(cashier.Token);
        var product = await db.CreateSellableProductAsync(30000m);
        await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(), CustomerId = customer,
            Lines = new[] { new { ProductUnitId = product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CREDIT", Amount = 30000m } },
        }, cashier.Token, device);

        var token = db.CashierToken();
        var byCode = await _client.GetAsync($"/api/v1/customers/search?q={code}", token);
        var byName = await _client.GetAsync("/api/v1/customers/search?q=rahayu%20siti", token);
        var byPhone = await _client.GetAsync("/api/v1/customers/search?q=99887766", token);

        var found = Assert.Single(byCode.Body.EnumerateArray());
        Assert.Equal(customer, found.Id());
        Assert.Equal(500000m, found.Dec("credit_limit"));
        Assert.Equal(30000m, found.Dec("credit_outstanding"));
        Assert.Equal(470000m, found.Dec("credit_remaining"));
        Assert.Equal(100, found.GetProperty("points").GetInt32());
        Assert.Contains(byName.Body.EnumerateArray(), c => c.Id() == customer);
        Assert.Contains(byPhone.Body.EnumerateArray(), c => c.Id() == customer);
    }

    [DatabaseFact]
    public async Task Customer_without_credit_facility_has_no_remaining_credit()
    {
        var customer = await db.CreateCustomerAsync(creditLimit: null);
        var code = await ScalarAsync<string>("SELECT code FROM contacts WHERE id = @customer", new { customer });

        var (_, body) = await _client.GetAsync($"/api/v1/customers/search?q={code}", db.CashierToken());

        var found = Assert.Single(body.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("credit_limit").ValueKind);
        Assert.Equal(JsonValueKind.Null, found.GetProperty("credit_remaining").ValueKind);
    }

    // ---- current shift and login info ---------------------------------------------------------

    [DatabaseFact]
    public async Task Current_shift_of_the_device()
    {
        var cashier = await UserAsync(Cashier10);
        var empty = await db.CreateDeviceAsync();
        var device = await OpenShiftAsync(cashier.Token);

        var (status, shift) = await _client.GetAsync("/api/v1/shifts/current", cashier.Token, device);
        var (noneStatus, none) = await _client.GetAsync("/api/v1/shifts/current", cashier.Token, empty);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(cashier.Id, shift.Id("cashier_id"));
        Assert.Equal((HttpStatusCode.NotFound, "no_open_shift"), (noneStatus, none.Str("code")));
    }

    [DatabaseFact]
    public async Task Login_response_names_the_node()
    {
        // a user of its own: no test logs in with a user another test relies on (lockout and cache stay separate)
        var name = "u" + Guid.NewGuid().ToString("N")[..12];
        await db.CreateUserAsync(name, "secret-1", roles: [("""["sale.create"]""", (Guid?)db.NodeId)]);
        var (_, body) = await _client.PostAsync("/api/v1/auth/login", new { Username = name, Password = "secret-1" });

        Assert.Equal("TK01", body.GetProperty("node").Str("code"));
        Assert.Equal("Toko Dev", body.GetProperty("node").Str("name"));
    }
}
