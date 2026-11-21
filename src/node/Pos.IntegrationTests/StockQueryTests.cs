using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Stock;
using Pos.Shared.Auth;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.IntegrationTests;

/// <summary>Form Stok Total / Form Cek Stok endpoints (permission stock.view).</summary>
[Collection(DatabaseCollection.Name)]
public class StockQueryTests(DatabaseFixture db) : IAsyncLifetime
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

    private static DateOnly Today => BranchClock.Today(DateTime.UtcNow, "Asia/Jakarta");

    /// <summary>A user of its own with only stock.view.</summary>
    private async Task<string> ViewerTokenAsync()
    {
        var name = "stk" + Guid.NewGuid().ToString("N")[..12];
        var id = await db.CreateUserAsync(name, "secret-1", roles: [("""["stock.view"]""", (Guid?)null)]);
        return db.TokenFor(id, name, [Permissions.StockView]);
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    /// <summary>Adds a BOX of 24 (largest unit) to the product.</summary>
    private async Task AddBoxAsync(Guid productId)
    {
        await ExecAsync(
            """
            INSERT INTO product_units (id, product_id, unit_id, conversion) SELECT @id, @productId, id, 24 FROM units WHERE code = 'BOX'
            """, new { id = Guid.CreateVersion7(), productId });
    }

    private async Task<JsonElement> TotalsAsync(string token, string query)
    {
        var (status, body) = await _client.GetAsync("/api/v1/stock/totals" + query, token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    [DatabaseFact]
    public async Task Locations_are_this_nodes_own_sellable_first()
    {
        var (status, body) = await _client.GetAsync("/api/v1/stock/locations", await ViewerTokenAsync());

        Assert.Equal(HttpStatusCode.OK, status);
        var codes = body.EnumerateArray().Select(l => l.Str("code")).ToList();
        Assert.Equal(["DISPLAY", "STORAGE"], codes);
        Assert.True(body[0].GetProperty("is_sellable").GetBoolean());
    }

    [DatabaseFact]
    public async Task Stock_total_has_one_row_per_product_and_location_with_the_largest_unit()
    {
        var sku = "STK-" + Guid.NewGuid().ToString("N")[..10];
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true, sku: sku, name: "Teh kotak stok total",
            stock: [(Today.AddDays(90), 5m), (Today.AddDays(200), 33m)], storageStock: 3m);
        await AddBoxAsync(product.ProductId);
        var token = await ViewerTokenAsync();

        var body = await TotalsAsync(token, $"?q={sku}");

        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var rows = body.GetProperty("items").EnumerateArray().ToList();
        var display = Assert.Single(rows, r => r.Str("location_code") == "DISPLAY");
        var storage = Assert.Single(rows, r => r.Str("location_code") == "STORAGE");
        Assert.Equal(38m, display.Dec("base_qty"));                     // 5 + 33 over two expiry dates
        Assert.Equal(3m, storage.Dec("base_qty"));
        Assert.Equal(("PCS", "BOX", 24m), (display.Str("base_unit_code"), display.Str("display_unit_code"), display.Dec("display_conversion")));
        Assert.Equal("Teh kotak stok total", display.Str("name"));
        Assert.False(display.TryGetProperty("cost", out _));            // no purchase price or HPP at a node
        Assert.False(display.TryGetProperty("purchase_price", out _));
    }

    [DatabaseFact]
    public async Task Search_by_name_words_barcode_and_location_filter()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var product = await db.CreateSellableProductAsync(1000m, name: $"Kopi bubuk {marker} sachet", stock: [(null, 4m)], storageStock: 6m);
        var token = await ViewerTokenAsync();

        var byWords = await TotalsAsync(token, $"?q=sachet%20{marker}");
        var byBarcode = await TotalsAsync(token, $"?q={product.Barcode}");
        var storageOnly = await TotalsAsync(token, $"?q={marker}&location_id={db.LocationB}");

        Assert.Equal(2, byWords.GetProperty("total").GetInt32());
        Assert.Equal(2, byBarcode.GetProperty("total").GetInt32());
        var only = Assert.Single(storageOnly.GetProperty("items").EnumerateArray());
        Assert.Equal((6m, "STORAGE"), (only.Dec("base_qty"), only.Str("location_code")));
        Assert.Equal("PCS", only.Str("display_unit_code"));             // no larger unit: the base unit
    }

    [DatabaseFact]
    public async Task Totals_are_paged()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++)
            await db.CreateSellableProductAsync(1000m, name: $"Paging {marker} {i}", stock: [(null, 1m)]);
        var token = await ViewerTokenAsync();

        var first = await TotalsAsync(token, $"?q={marker}&limit=2");
        var last = await TotalsAsync(token, $"?q={marker}&limit=2&offset=4");

        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal(["Paging " + marker + " 0", "Paging " + marker + " 1"],
            first.GetProperty("items").EnumerateArray().Select(r => r.Str("name")));
        Assert.Equal("Paging " + marker + " 4", Assert.Single(last.GetProperty("items").EnumerateArray()).Str("name"));
    }

    [DatabaseFact]
    public async Task Other_nodes_stock_is_not_listed()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var product = await db.CreateSellableProductAsync(1000m, name: $"Lintas node {marker}", stock: [(null, 2m)]);
        var otherNode = await db.CreateOtherNodeAsync();
        var otherLocation = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO locations (id, node_id, code, name, type, is_sellable) VALUES (@otherLocation, @otherNode, 'DISPLAY', 'Display', 'DISPLAY', 1)",
            new { otherLocation, otherNode });
        await new StockPostingService(db.Factory).PostAsync([
            new StockMovement(new StockDocumentLine(otherNode, otherLocation, product.ProductId, StockDocTypes.Adjustment,
                Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow), StockDirection.In, 50m)]);

        var body = await TotalsAsync(await ViewerTokenAsync(), $"?q={marker}");

        var row = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(2m, row.Dec("base_qty"));
    }

    [DatabaseFact]
    public async Task Cek_stok_lists_expiry_dates_with_near_and_expired_markers()
    {
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true,
            stock: [(Today.AddDays(-3), 2m), (Today.AddDays(10), 4m), (Today.AddDays(120), 6m)]);

        var (status, body) = await _client.GetAsync($"/api/v1/stock/products/{product.ProductId}/expiry", await ViewerTokenAsync());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(30, body.GetProperty("warning_days").GetInt32());
        Assert.Equal(Today.ToString("yyyy-MM-dd"), body.Str("today"));
        var rows = body.GetProperty("rows").EnumerateArray().Select(r => (r.Str("expiry_date"), r.Dec("base_qty"), r.Str("status"))).ToList();
        Assert.Equal(
        [
            (Today.AddDays(-3).ToString("yyyy-MM-dd"), 2m, "expired"),
            (Today.AddDays(10).ToString("yyyy-MM-dd"), 4m, "near_expiry"),
            (Today.AddDays(120).ToString("yyyy-MM-dd"), 6m, "ok"),
        ], rows);
    }

    [DatabaseFact]
    public async Task Branch_setting_changes_the_warning_days_and_no_expiry_stock_has_no_marker()
    {
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true, stock: [(Today.AddDays(10), 4m)], storageStock: 7m);
        await ExecAsync("INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@id, @BranchId, 'stock.expiry_warning_days', '5')",
            new { id = Guid.CreateVersion7(), db.BranchId });
        try
        {
            var (_, body) = await _client.GetAsync($"/api/v1/stock/products/{product.ProductId}/expiry", await ViewerTokenAsync());

            Assert.Equal(5, body.GetProperty("warning_days").GetInt32());
            var rows = body.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("ok", Assert.Single(rows, r => r.Str("location_code") == "DISPLAY").Str("status"));   // 10 days > 5
            var noExpiry = Assert.Single(rows, r => r.Str("location_code") == "STORAGE");
            Assert.Equal(JsonValueKind.Null, noExpiry.GetProperty("expiry_date").ValueKind);
            Assert.Equal("no_expiry", noExpiry.Str("status"));
        }
        finally
        {
            await ExecAsync("DELETE FROM settings WHERE branch_id = @BranchId AND setting_key = 'stock.expiry_warning_days'", new { db.BranchId });
        }
    }

    [DatabaseFact]
    public async Task Stock_screens_need_stock_view()
    {
        var unknown = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/stock/totals", token: null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/stock/totals", db.CashierToken())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/stock/locations", db.CashierToken())).Status);
        var (status, body) = await _client.GetAsync($"/api/v1/stock/products/{unknown}/expiry", await ViewerTokenAsync());
        Assert.Equal((HttpStatusCode.NotFound, "unknown_product"), (status, body.Str("code")));
    }

    [DatabaseFact]
    public async Task Seeded_store_admin_has_stock_view_and_the_cashier_does_not()
    {
        await using var connection = await db.Factory.OpenAsync();
        var roles = (await connection.QueryAsync<(string Code, string Permissions)>("SELECT code, CAST(permissions AS CHAR) FROM roles"))
            .ToDictionary(r => r.Code, r => PermissionSet.FromRoleJson([r.Permissions]));

        Assert.True(roles["STORE_ADMIN"].Has(Permissions.StockView));
        Assert.False(roles["CASHIER"].Has(Permissions.StockView));
    }
}
