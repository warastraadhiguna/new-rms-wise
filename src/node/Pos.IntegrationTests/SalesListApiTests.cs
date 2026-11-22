using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Time;

namespace Pos.IntegrationTests;

/// <summary>Data Penjualan (10a): columns, status, search, period, cashier history limit.</summary>
[Collection(DatabaseCollection.Name)]
public class SalesListApiTests(DatabaseFixture db) : IAsyncLifetime
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

    private sealed record User(Guid Id, string Name, string Token);

    private async Task<User> NewUserAsync(params string[] permissions)
    {
        var name = $"sl{Interlocked.Increment(ref _users)}_{Guid.NewGuid():N}"[..30];
        var id = await db.CreateUserAsync(name, "secret-1", roles: (JsonSerializer.Serialize(permissions), db.NodeId));
        return new User(id, name, db.TokenFor(id, name, permissions));
    }

    private static DateOnly Today => BranchClock.Today(DateTime.UtcNow, "Asia/Jakarta");

    private async Task<Guid> OpenShiftAsync(User cashier)
    {
        var device = await db.CreateDeviceAsync();
        var (status, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, cashier.Token, device);
        Assert.Equal(HttpStatusCode.Created, status);
        return device;
    }

    private async Task<JsonElement> SellAsync(User cashier, Guid device, TestProduct product, decimal qty, object[] payments,
        Guid? customer = null, decimal discountPct = 0m)
    {
        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            DiscountPct = discountPct,
            Lines = new[] { new { product.ProductUnitId, Qty = qty, DiscountPct = 0m, DiscountAmount = 0m } },
            Payments = payments,
        }, cashier.Token, device);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return body.GetProperty("sale");
    }

    private static object Pay(string method, decimal amount) => new { Method = method, Amount = amount };

    private Task<(HttpStatusCode Status, JsonElement Body)> List(User user, string query) =>
        _client.GetAsync("/api/v1/sales" + query, user.Token);

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    [DatabaseFact]
    public async Task Rows_show_the_rms2_columns_status_and_outstanding_credit()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var device = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 50m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 10000000m);

        var cash = await SellAsync(cashier, device, product, 2m, [Pay("CASH", 20000m)]);
        var credit = await SellAsync(cashier, device, product, 6m, [Pay("CASH", 15000m), Pay("CREDIT", 45000m)], customer);
        var voided = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 10000m)]);
        var reason = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO reasons (id, type, name) VALUES (@reason, 'SALE_VOID', 'Uji')", new { reason });
        await ExecAsync(
            "INSERT INTO sale_voids (id, node_id, sale_id, reason_id, voided_at, created_by) VALUES (@id, @NodeId, @sale, @reason, UTC_TIMESTAMP(3), @user)",
            new { id = Guid.CreateVersion7(), db.NodeId, sale = voided.Id(), reason, user = cashier.Id });

        var (status, page) = await List(cashier, $"?q={cashier.Name}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal("Asia/Jakarta", page.Str("time_zone"));
        Assert.Equal(Today.AddDays(-15).ToString("yyyy-MM-dd"), page.Str("earliest_date"));
        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(r => r.Id());
        Assert.Equal([voided.Id(), credit.Id(), cash.Id()], page.GetProperty("items").EnumerateArray().Select(r => r.Id()));  // newest first

        var c = rows[credit.Id()];
        Assert.Equal(credit.Str("number"), c.Str("number"));
        Assert.Equal("Test customer", c.Str("customer_name"));
        Assert.Equal("Test user", c.Str("cashier_name"));
        Assert.Equal("TUNAI + KREDIT", c.Str("payment_methods"));
        Assert.Equal((60000m, 60000m, 45000m, 45000m), (c.Dec("total"), c.Dec("grand_total"), c.Dec("credit_amount"), c.Dec("outstanding")));
        Assert.Equal("UNPAID", c.Str("status"));
        Assert.Equal(JsonValueKind.String, c.GetProperty("due_date").ValueKind);
        Assert.True(c.GetProperty("shift_open").GetBoolean());

        Assert.Equal(("PAID", "TUNAI", 0m), (rows[cash.Id()].Str("status"), rows[cash.Id()].Str("payment_methods"), rows[cash.Id()].Dec("outstanding")));
        Assert.Equal("VOID", rows[voided.Id()].Str("status"));
    }

    [DatabaseFact]
    public async Task Search_by_nota_member_or_cashier_and_paging()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var device = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 50m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m);
        var marker = "Pelanggan " + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync("UPDATE contacts SET name = @marker WHERE id = @customer", new { marker, customer });

        var first = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);
        await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)], customer);
        await SellAsync(cashier, device, product, 1m, [Pay("QRIS", 5000m)]);

        var byNumber = (await List(cashier, $"?q={first.Str("number")}")).Body;
        var byMember = (await List(cashier, $"?q={Uri.EscapeDataString(marker)}")).Body;
        var byCashier = (await List(cashier, $"?q={cashier.Name}&limit=2")).Body;
        var secondPage = (await List(cashier, $"?q={cashier.Name}&limit=2&offset=2")).Body;

        Assert.Equal([first.Id()], byNumber.GetProperty("items").EnumerateArray().Select(r => r.Id()));
        Assert.Equal(1, byMember.GetProperty("total").GetInt32());
        Assert.Equal((3, 2), (byCashier.GetProperty("total").GetInt32(), byCashier.GetProperty("items").GetArrayLength()));
        Assert.Equal(first.Id(), Assert.Single(secondPage.GetProperty("items").EnumerateArray()).Id());
    }

    [DatabaseFact]
    public async Task The_period_uses_branch_dates()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var device = await OpenShiftAsync(cashier);
        var product = await db.CreateSellableProductAsync(5000m, stock: [(null, 50m)]);
        var old = await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);
        await SellAsync(cashier, device, product, 1m, [Pay("CASH", 5000m)]);
        // 3 days ago, 23:30 WIB (16:30 UTC)
        var threeDaysAgo = Today.AddDays(-3).ToDateTime(new TimeOnly(16, 30), DateTimeKind.Utc);
        await ExecAsync("UPDATE sales SET sold_at = @threeDaysAgo WHERE id = @id", new { threeDaysAgo, id = old.Id() });

        var todayOnly = (await List(cashier, $"?q={cashier.Name}")).Body;
        var thatDay = (await List(cashier, $"?q={cashier.Name}&from={Today.AddDays(-3):yyyy-MM-dd}&to={Today.AddDays(-3):yyyy-MM-dd}")).Body;

        Assert.Equal(1, todayOnly.GetProperty("total").GetInt32());
        Assert.Equal(old.Id(), Assert.Single(thatDay.GetProperty("items").EnumerateArray()).Id());
    }

    [DatabaseFact]
    public async Task A_cashier_sees_only_h_minus_15_and_sale_history_has_no_limit()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var admin = await NewUserAsync("sale.create", "sale.history");
        var warehouse = await NewUserAsync("stock.view");

        var (tooOld, tooOldBody) = await List(cashier, $"?from={Today.AddDays(-16):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var (limit, _) = await List(cashier, $"?from={Today.AddDays(-15):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var (adminStatus, adminBody) = await List(admin, $"?from={Today.AddDays(-400):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var (forbidden, _) = await List(warehouse, "");

        Assert.Equal((HttpStatusCode.Forbidden, "history_limit"), (tooOld, tooOldBody.Str("code")));
        Assert.Equal(Today.AddDays(-15).ToString("yyyy-MM-dd"), tooOldBody.Str("earliest_date"));
        Assert.Equal(HttpStatusCode.OK, limit);
        Assert.Equal(HttpStatusCode.OK, adminStatus);
        Assert.Equal(JsonValueKind.Null, adminBody.GetProperty("earliest_date").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
    }

    [DatabaseFact]
    public async Task The_history_limit_follows_the_branch_setting()
    {
        var cashier = await NewUserAsync("shift.open", "sale.create");
        var setting = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@setting, @BranchId, 'sales.cashier_history_days', '3')",
            new { setting, db.BranchId });
        try
        {
            var (status, body) = await List(cashier, $"?from={Today.AddDays(-4):yyyy-MM-dd}");
            var (ok, _) = await List(cashier, $"?from={Today.AddDays(-3):yyyy-MM-dd}");

            Assert.Equal((HttpStatusCode.Forbidden, "history_limit"), (status, body.Str("code")));
            Assert.Equal(HttpStatusCode.OK, ok);
        }
        finally
        {
            await ExecAsync("DELETE FROM settings WHERE id = @setting", new { setting });
        }
    }

    [DatabaseFact]
    public async Task Invalid_range_and_paging_are_rejected()
    {
        var cashier = await NewUserAsync("sale.create");

        var (range, rangeBody) = await List(cashier, $"?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");
        var (paging, pagingBody) = await List(cashier, "?limit=5000");

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_range"), (range, rangeBody.Str("code")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_paging"), (paging, pagingBody.Str("code")));
    }
}
