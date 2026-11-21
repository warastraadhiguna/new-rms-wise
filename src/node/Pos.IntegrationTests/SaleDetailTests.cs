using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Dev;
using Pos.Shared.Auth;

namespace Pos.IntegrationTests;

/// <summary>GET /api/v1/sales/{id}: what the desktop prints on the receipt (after payment and on F5 reprint).</summary>
[Collection(DatabaseCollection.Name)]
public class SaleDetailTests(DatabaseFixture db) : IAsyncLifetime
{
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

    private const string SupervisorPassword = "secret-1";

    /// <summary>A supervisor of its own for each test, so no test can lock out or change a user another test uses.</summary>
    private async Task<string> SupervisorAsync()
    {
        var name = "spv" + Guid.NewGuid().ToString("N")[..12];
        await db.CreateUserAsync(name, SupervisorPassword, roles: [("[\"sale.discount.override\"]", (Guid?)null)]);
        return name;
    }

    private async Task<Guid> OpenShiftAsync()
    {
        var device = await db.CreateDeviceAsync();
        var (status, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 100000m }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return device;
    }

    private async Task<Guid> SellAsync(object body, Guid device)
    {
        var (status, sale) = await _client.PostAsync("/api/v1/sales", body, db.CashierToken(), device);
        Assert.True(status == HttpStatusCode.Created, sale.ToString());
        return sale.GetProperty("sale").Id();
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    [DatabaseFact]
    public async Task Cash_sale_with_percent_discount_has_names_cashier_payment_and_receipt_texts()
    {
        var product = await db.CreateSellableProductAsync(1000m, stock: [(null, 50m)], name: "Teh botol kotak 250 ml");
        var device = await OpenShiftAsync();
        var id = await SellAsync(new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = 3m } },
            Payments = new[] { new { Method = "CASH", Amount = 5000m } },
            DiscountPct = 10m,
            SupervisorUsername = await SupervisorAsync(),
            SupervisorPassword = SupervisorPassword,
        }, device);

        var (status, sale) = await _client.GetAsync($"/api/v1/sales/{id}", db.CashierToken());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("TK01-SL-", sale.Str("number"));
        Assert.Equal("Asia/Jakarta", sale.Str("time_zone"));
        Assert.Equal("Kasir Satu", sale.GetProperty("cashier").Str("full_name"));
        Assert.Equal(JsonValueKind.Null, sale.GetProperty("customer").ValueKind);
        Assert.Equal(3000m, sale.Dec("subtotal"));
        Assert.Equal(300m, sale.Dec("discount_amount"));
        Assert.Equal(10m, sale.Dec("discount_pct"));
        Assert.Equal(2700m, sale.Dec("total"));
        Assert.Equal(2300m, sale.Dec("change"));

        var line = Assert.Single(sale.GetProperty("lines").EnumerateArray());
        Assert.Equal("Teh botol kotak 250 ml", line.Str("name"));
        Assert.Equal("PCS", line.Str("unit_code"));
        Assert.Equal(3m, line.Dec("qty"));
        Assert.Equal(1000m, line.Dec("unit_price"));
        Assert.Equal(3000m, line.Dec("line_total"));

        var payment = Assert.Single(sale.GetProperty("payments").EnumerateArray());
        Assert.Equal("CASH", payment.Str("method"));
        Assert.Equal("TUNAI", payment.Str("method_name"));
        Assert.True(payment.GetProperty("is_cash").GetBoolean());
        Assert.Equal(5000m, payment.Dec("tendered"));
        Assert.Equal(2300m, payment.Dec("change_given"));

        var receipt = sale.GetProperty("receipt");
        Assert.Equal("TOKO DEV", receipt.Str("store_name"));
        Assert.Equal("Jl. Merdeka No. 1", receipt.Str("address"));
        Assert.Equal("Jakarta", receipt.Str("city"));
        Assert.Equal("021-5550123", receipt.Str("phone"));
        Assert.Equal("Terimakasih Atas Kunjungannya", receipt.Str("footer_1"));
        Assert.Equal("Sampai Jumpa Kembali", receipt.Str("footer_2"));
        Assert.False(receipt.TryGetProperty("header_lines", out _));
    }

    [DatabaseFact]
    public async Task Amount_discount_has_no_percent_and_credit_sale_has_customer_and_due_date()
    {
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 10m)]);
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m, paymentTermDays: 14);
        var device = await OpenShiftAsync();
        var id = await SellAsync(new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            Lines = new[] { new { product.ProductUnitId, Qty = 2m } },
            Payments = new[] { new { Method = "CREDIT", Amount = 19000m } },
            DiscountAmount = 1000m,
            SupervisorUsername = await SupervisorAsync(),
            SupervisorPassword = SupervisorPassword,
        }, device);

        var (_, sale) = await _client.GetAsync($"/api/v1/sales/{id}", db.CashierToken());

        Assert.Equal(1000m, sale.Dec("discount_amount"));
        Assert.Equal(JsonValueKind.Null, sale.GetProperty("discount_pct").ValueKind);
        Assert.Equal(19000m, sale.Dec("credit_amount"));
        Assert.Equal("Test customer", sale.GetProperty("customer").Str("name"));
        Assert.Equal(JsonValueKind.String, sale.GetProperty("due_date").ValueKind);
        var payment = Assert.Single(sale.GetProperty("payments").EnumerateArray());
        Assert.Equal("KREDIT", payment.Str("method_name"));
        Assert.True(payment.GetProperty("is_credit").GetBoolean());
    }

    [DatabaseFact]
    public async Task Branch_receipt_settings_win_over_global_ones()
    {
        var product = await db.CreateSellableProductAsync(1000m, stock: [(null, 5m)]);
        var device = await OpenShiftAsync();
        var id = await SellAsync(new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = 1000m } },
        }, device);
        await ExecAsync(
            """
            INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES
              (@a, @BranchId, 'receipt.store_name', '"TOKO DEV CABANG 2"'),
              (@b, @BranchId, 'receipt.footer_2', '""')
            """, new { a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), db.BranchId });
        try
        {
            var (_, sale) = await _client.GetAsync($"/api/v1/sales/{id}", db.CashierToken());

            var receipt = sale.GetProperty("receipt");
            Assert.Equal("TOKO DEV CABANG 2", receipt.Str("store_name"));
            Assert.Equal("Jakarta", receipt.Str("city"));                                 // global value still used
            Assert.Equal("", receipt.Str("footer_2"));                                    // branch blanked it: not printed
            Assert.Equal("Terimakasih Atas Kunjungannya", receipt.Str("footer_1"));
        }
        finally
        {
            await ExecAsync("DELETE FROM settings WHERE branch_id = @BranchId AND setting_key LIKE 'receipt.%'", new { db.BranchId });
        }
    }

    [DatabaseFact]
    public async Task Missing_receipt_settings_fall_back_to_the_store_name_and_the_rms2_thank_you_line()
    {
        var product = await db.CreateSellableProductAsync(1000m, stock: [(null, 5m)]);
        var device = await OpenShiftAsync();
        var id = await SellAsync(new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = 1000m } },
        }, device);
        await ExecAsync("UPDATE settings SET deleted_at = UTC_TIMESTAMP(3) WHERE setting_key LIKE 'receipt.%' AND deleted_at IS NULL", new { });
        try
        {
            var (_, sale) = await _client.GetAsync($"/api/v1/sales/{id}", db.CashierToken());

            var receipt = sale.GetProperty("receipt");
            Assert.Equal("Toko Dev", receipt.Str("store_name"));
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("address").ValueKind);
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("phone").ValueKind);
            Assert.Equal("Terimakasih Atas Kunjungannya", receipt.Str("footer_1"));
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("footer_2").ValueKind);
        }
        finally
        {
            await ExecAsync(
                "UPDATE settings SET deleted_at = NULL WHERE setting_key IN ('receipt.store_name','receipt.address','receipt.city','receipt.phone','receipt.footer_1','receipt.footer_2')",
                new { });
        }
    }

    [DatabaseFact]
    public async Task Dev_seed_replaces_the_old_receipt_line_settings()
    {
        await ExecAsync(
            "INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@id, NULL, 'receipt.header_lines', '[\"LAMA\"]')",
            new { id = Guid.CreateVersion7() });

        await new DevSeeder(new Pos.NodeService.Data.TransactionRunner(db.Factory), TimeProvider.System).SeedAsync("TK01");

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM settings WHERE setting_key IN ('receipt.header_lines','receipt.footer_lines') AND deleted_at IS NULL"));
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM settings WHERE setting_key LIKE 'receipt.%' AND branch_id IS NULL AND deleted_at IS NULL"));
    }

    [DatabaseFact]
    public async Task Unknown_sale_is_404_and_login_is_required()
    {
        var unknown = Guid.CreateVersion7();

        var (status, body) = await _client.GetAsync($"/api/v1/sales/{unknown}", db.CashierToken());
        var (anonymous, _) = await _client.GetAsync($"/api/v1/sales/{unknown}", token: null);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("unknown_sale", body.Str("code"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous);
    }

    [DatabaseFact]
    public async Task Seed_gives_stock_rebuild_only_to_owner_and_hq_admin()
    {
        await using var connection = await db.Factory.OpenAsync();
        var roles = (await connection.QueryAsync<(string Code, string Permissions)>("SELECT code, CAST(permissions AS CHAR) FROM roles"))
            .ToDictionary(r => r.Code, r => PermissionSet.FromRoleJson([r.Permissions]));
        var adminRole = await connection.ExecuteScalarAsync<string>(
            "SELECT r.code FROM user_roles ur JOIN users u ON u.id = ur.user_id JOIN roles r ON r.id = ur.role_id WHERE u.username = 'admin'");

        Assert.True(roles["OWNER"].Has(Permissions.StockRebuild));
        Assert.True(roles["HQ_ADMIN"].Has(Permissions.StockRebuild));
        Assert.False(roles["STORE_ADMIN"].Has(Permissions.StockRebuild));
        Assert.False(roles["CASHIER"].Has(Permissions.StockRebuild));
        Assert.True(roles["STORE_ADMIN"].Has(Permissions.SaleDiscountOverride));
        Assert.True(roles["STORE_ADMIN"].Has(Permissions.SaleLineDelete));
        Assert.Equal("OWNER", adminRole);

        var (_, login) = await _client.PostAsync("/api/v1/auth/login", new { Username = "admintoko", Password = DevCredentials.StoreAdminPassword });
        var (rebuild, _) = await _client.PostAsync("/api/v1/admin/stock/integrity-check", new { }, login.Str("access_token"));
        Assert.Equal(HttpStatusCode.Forbidden, rebuild);
    }
}
