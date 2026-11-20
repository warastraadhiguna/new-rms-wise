using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

/// <summary>Backend for the sales screen: cart cancellations (F12), product units, extra search columns.</summary>
[Collection(DatabaseCollection.Name)]
public class SalesScreenBackendTests(DatabaseFixture db) : IAsyncLifetime
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

    private async Task<Guid> OpenShiftAsync(string? token = null)
    {
        var device = await db.CreateDeviceAsync();
        var (status, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token ?? db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return device;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Cancel(Guid device, object body, string? token = null) =>
        _client.PostAsync("/api/v1/cart-cancellations", body, token ?? db.CashierToken(), device);

    private static object Body(Guid id, string? reason, params (Guid ProductUnitId, decimal Qty, decimal Price)[] lines) => new
    {
        Id = id,
        Reason = reason,
        TotalAmount = lines.Sum(l => l.Qty * l.Price),
        Lines = lines.Select(l => new { l.ProductUnitId, l.Qty, UnitPrice = l.Price, LineTotal = l.Qty * l.Price }).ToArray(),
    };

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    [DatabaseFact]
    public async Task Cart_cancellation_is_saved_with_lines_number_and_outbox()
    {
        var device = await OpenShiftAsync();
        var a = await db.CreateSellableProductAsync(7500m);
        var b = await db.CreateSellableProductAsync(4500m);
        var id = Guid.CreateVersion7();

        var (status, body) = await Cancel(device, Body(id, "Pelanggan batal membeli", (a.ProductUnitId, 2m, 7500m), (b.ProductUnitId, 1m, 4500m)));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Matches(@"^TK01-BT-\d{6}-\d{6}$", body.Str("number"));
        Assert.Equal("Pelanggan batal membeli", body.Str("reason"));
        Assert.Equal(19500m, body.Dec("total_amount"));
        Assert.Equal(db.Seed.CashierId, body.Id("cashier_id"));
        Assert.Equal(2, body.GetProperty("lines").GetArrayLength());

        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM cart_cancellation_lines WHERE cart_cancellation_id = @id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_type = @type AND doc_id = @id", new { type = SyncDocTypes.CartCancellation, id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM stock_ledger WHERE doc_id = @id", new { id }));   // no stock effect
    }

    [DatabaseFact]
    public async Task Same_cancellation_id_again_returns_the_stored_document()
    {
        var device = await OpenShiftAsync();
        var a = await db.CreateSellableProductAsync(1000m);
        var id = Guid.CreateVersion7();

        var first = await Cancel(device, Body(id, "Salah input", (a.ProductUnitId, 1m, 1000m)));
        var again = await Cancel(device, Body(id, "Salah input", (a.ProductUnitId, 1m, 1000m)));

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(first.Body.Str("number"), again.Body.Str("number"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM cart_cancellations WHERE id = @id", new { id }));
    }

    [DatabaseFact]
    public async Task Cancellation_needs_reason_lines_valid_id_and_known_units()
    {
        var device = await OpenShiftAsync();
        var a = await db.CreateSellableProductAsync(1000m);

        var noReason = await Cancel(device, Body(Guid.CreateVersion7(), "   ", (a.ProductUnitId, 1m, 1000m)));
        var noLines = await Cancel(device, Body(Guid.CreateVersion7(), "Batal"));
        var badId = await Cancel(device, Body(Guid.NewGuid(), "Batal", (a.ProductUnitId, 1m, 1000m)));
        var unknown = await Cancel(device, Body(Guid.CreateVersion7(), "Batal", (Guid.CreateVersion7(), 1m, 1000m)));
        var zeroQty = await Cancel(device, Body(Guid.CreateVersion7(), "Batal", (a.ProductUnitId, 0m, 1000m)));

        Assert.Equal("reason_required", noReason.Body.Str("code"));
        Assert.Equal("no_lines", noLines.Body.Str("code"));
        Assert.Equal("invalid_id", badId.Body.Str("code"));
        Assert.Equal("unknown_product_unit", unknown.Body.Str("code"));
        Assert.Equal("invalid_quantity", zeroQty.Body.Str("code"));
        Assert.All(new[] { noReason, noLines, badId, unknown, zeroQty }, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status));
    }

    [DatabaseFact]
    public async Task Cancellation_needs_an_own_open_shift()
    {
        var a = await db.CreateSellableProductAsync(1000m);
        var noShiftDevice = await db.CreateDeviceAsync();
        var othersDevice = await OpenShiftAsync(db.TokenFor(db.Seed.AdminId, "admin", ["shift.open"]));

        var noShift = await Cancel(noShiftDevice, Body(Guid.CreateVersion7(), "Batal", (a.ProductUnitId, 1m, 1000m)));
        var notOwner = await Cancel(othersDevice, Body(Guid.CreateVersion7(), "Batal", (a.ProductUnitId, 1m, 1000m)));
        var anonymous = await _client.PostAsync("/api/v1/cart-cancellations", Body(Guid.CreateVersion7(), "Batal"), token: null, device: noShiftDevice);

        Assert.Equal((HttpStatusCode.Conflict, "no_open_shift"), (noShift.Status, noShift.Body.Str("code")));
        Assert.Equal((HttpStatusCode.Forbidden, "not_shift_owner"), (notOwner.Status, notOwner.Body.Str("code")));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    [DatabaseFact]
    public async Task Product_units_lists_every_unit_with_its_price()
    {
        var water = db.Seed.Products.Single(p => p.Sku == "AIR-600");

        var (status, body) = await _client.GetAsync($"/api/v1/products/{water.ProductId}/units", db.CashierToken());

        Assert.Equal(HttpStatusCode.OK, status);
        var units = body.EnumerateArray().ToList();
        Assert.Equal(["PCS", "BOX"], units.Select(u => u.Str("unit_code")));
        Assert.Equal(4000m, units[0].Dec("price"));
        Assert.Equal(80000m, units[1].Dec("price"));
        Assert.Equal(24m, units[1].Dec("conversion"));
        Assert.All(units, u => Assert.Equal(water.ProductId, u.Id("product_id")));
    }

    [DatabaseFact]
    public async Task Product_units_of_unknown_product_is_404()
    {
        var (status, body) = await _client.GetAsync($"/api/v1/products/{Guid.CreateVersion7()}/units", db.CashierToken());

        Assert.Equal((HttpStatusCode.NotFound, "unknown_product"), (status, body.Str("code")));
    }

    [DatabaseFact]
    public async Task Search_returns_shelf_location_and_discount_price_column()
    {
        var product = await db.CreateSellableProductAsync(2500m);
        await using (var connection = await db.Factory.OpenAsync())
            await connection.ExecuteAsync("UPDATE products SET shelf_location = 'Rak A-3' WHERE id = @ProductId", new { product.ProductId });

        var (_, body) = await _client.GetAsync($"/api/v1/products/search?q={product.Barcode}", db.CashierToken());

        var item = Assert.Single(body.EnumerateArray());
        Assert.Equal("Rak A-3", item.Str("shelf_location"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("discount_price").ValueKind);   // promotions: TODO
    }
}
