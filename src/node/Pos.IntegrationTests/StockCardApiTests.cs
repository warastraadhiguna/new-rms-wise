using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Stock;
using Pos.Shared.Auth;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.IntegrationTests;

/// <summary>Kartu Stok endpoint: opening balance, running balance, closing = current stock, paging, labels.</summary>
[Collection(DatabaseCollection.Name)]
public class StockCardApiTests(DatabaseFixture db) : IAsyncLifetime
{
    private static int _adjustments;
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

    private async Task<string> ViewerTokenAsync()
    {
        var name = "kartu" + Guid.NewGuid().ToString("N")[..12];
        var id = await db.CreateUserAsync(name, "secret-1", roles: [("""["stock.view"]""", (Guid?)null)]);
        return db.TokenFor(id, name, [Permissions.StockView]);
    }

    /// <summary>A ledger movement at a given UTC time, optionally in a real stock_adjustments document by the admin.</summary>
    private async Task<Guid> MoveAsync(Guid productId, decimal qty, DateTime occurredAtUtc, Guid? location = null, bool withDocument = false)
    {
        var docId = Guid.CreateVersion7();
        await new StockPostingService(db.Factory).PostAsync([
            new StockMovement(new StockDocumentLine(db.NodeId, location ?? db.LocationA, productId, StockDocTypes.Adjustment,
                docId, Guid.CreateVersion7(), occurredAtUtc), qty > 0 ? StockDirection.In : StockDirection.Out, Math.Abs(qty))]);
        if (withDocument)
        {
            await using var connection = await db.Factory.OpenAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO stock_adjustments (id, node_id, number, location_id, reason_id, adjusted_at, created_by)
                VALUES (@docId, @NodeId, @number, @LocationA, (SELECT MIN(id) FROM reasons), @occurredAtUtc, @AdminId)
                """, new { docId, db.NodeId, number = $"TK01-ADJ-TEST-{Interlocked.Increment(ref _adjustments):000000}", db.LocationA, occurredAtUtc, db.Seed.AdminId });
        }
        return docId;
    }

    private async Task<(Guid Id, string Number)> SellAsync(TestProduct product, decimal qty)
    {
        var device = await db.CreateDeviceAsync();
        var (opened, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, opened);
        var (status, body) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = qty } },
            Payments = new[] { new { Method = "CASH", Amount = 1000000m } },
        }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (body.GetProperty("sale").Id(), body.GetProperty("sale").Str("number"));
    }

    private async Task<decimal> CurrentStockAsync(Guid productId, Guid? location = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(sb.base_qty), 0) FROM stock_balances sb JOIN locations l ON l.id = sb.location_id
            WHERE sb.product_id = @productId AND l.node_id = @NodeId AND (@location IS NULL OR sb.location_id = @location)
            """, new { productId, db.NodeId, location });
    }

    private async Task<JsonElement> CardAsync(string token, string query)
    {
        var (status, body) = await _client.GetAsync("/api/v1/stock/card" + query, token);
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        return body;
    }

    [DatabaseFact]
    public async Task Closing_balance_equals_the_current_stock()
    {
        var product = await db.CreateSellableProductAsync(1000m, stock: [(null, 40m)], storageStock: 7m);
        await MoveAsync(product.ProductId, -3m, DateTime.UtcNow.AddMinutes(-5));
        await SellAsync(product, 5m);
        await MoveAsync(product.ProductId, 2m, DateTime.UtcNow.AddMinutes(-1), db.LocationB);

        var card = await CardAsync(await ViewerTokenAsync(), $"?product_id={product.ProductId}&from=2020-01-01&to={Today:yyyy-MM-dd}");

        var current = await CurrentStockAsync(product.ProductId);
        Assert.Equal(41m, current);                                                  // 40 + 7 - 3 - 5 + 2
        Assert.Equal(current, card.Dec("closing_balance"));
        Assert.Equal(0m, card.Dec("opening_balance"));
        var rows = card.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(current, rows[^1].Dec("balance"));
        Assert.Equal(card.Dec("total_in") - card.Dec("total_out"), card.Dec("closing_balance"));
        Assert.Equal(rows.Count, card.GetProperty("total_rows").GetInt32());
    }

    [DatabaseFact]
    public async Task Opening_balance_is_the_stock_before_the_start_date_and_the_running_balance_continues_from_it()
    {
        var product = await db.CreateSellableProductAsync(1000m);
        await MoveAsync(product.ProductId, 30m, new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc));
        await MoveAsync(product.ProductId, -4m, new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc));
        await MoveAsync(product.ProductId, 10m, new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc));
        await MoveAsync(product.ProductId, -6m, new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc));

        var card = await CardAsync(await ViewerTokenAsync(), $"?product_id={product.ProductId}&from=2026-11-01&to={Today:yyyy-MM-dd}");

        Assert.Equal(26m, card.Dec("opening_balance"));                              // 30 - 4 before 1 Nov
        var rows = card.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([(10m, 0m, 36m), (0m, 6m, 30m)], rows.Select(r => (r.Dec("qty_in"), r.Dec("qty_out"), r.Dec("balance"))));
        Assert.Equal((10m, 6m, 30m), (card.Dec("total_in"), card.Dec("total_out"), card.Dec("closing_balance")));
        Assert.Equal(await CurrentStockAsync(product.ProductId), card.Dec("closing_balance"));
    }

    [DatabaseFact]
    public async Task Rows_show_type_number_user_location_and_expiry()
    {
        var expiry = Today.AddDays(200);
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true, stock: [(expiry, 20m)]);
        var adjustment = await MoveAsync(product.ProductId, 1m, DateTime.UtcNow.AddMinutes(-2), withDocument: true);
        var sale = await SellAsync(product, 3m);

        var card = await CardAsync(await ViewerTokenAsync(), $"?product_id={product.ProductId}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Equal("Asia/Jakarta", card.Str("time_zone"));
        var rows = card.GetProperty("rows").EnumerateArray().ToList();
        var saleRow = Assert.Single(rows, r => r.Str("doc_type") == "SALE");
        Assert.Equal(("Penjualan", sale.Number, "Kasir Satu", "DISPLAY"), (saleRow.Str("doc_label"), saleRow.Str("doc_number"), saleRow.Str("user_name"), saleRow.Str("location_code")));
        Assert.Equal((0m, 3m), (saleRow.Dec("qty_in"), saleRow.Dec("qty_out")));
        Assert.Equal(expiry.ToString("yyyy-MM-dd"), saleRow.Str("expiry_date"));
        Assert.Equal(sale.Id, saleRow.Id("doc_id"));

        var adjustmentRow = Assert.Single(rows, r => r.Id("doc_id") == adjustment);
        Assert.Equal("Penyesuaian", adjustmentRow.Str("doc_label"));
        Assert.StartsWith("TK01-ADJ-TEST-", adjustmentRow.Str("doc_number"));
        Assert.Equal("Admin Dev", adjustmentRow.Str("user_name"));
        Assert.False(adjustmentRow.TryGetProperty("cost", out _));
    }

    [DatabaseFact]
    public async Task Pages_carry_the_true_running_balance()
    {
        var product = await db.CreateSellableProductAsync(1000m);
        var start = DateTime.UtcNow.AddHours(-2);
        decimal[] moves = [5m, 3m, -2m, 4m, -1m, 6m, -3m];
        for (var i = 0; i < moves.Length; i++)
            await MoveAsync(product.ProductId, moves[i], start.AddMinutes(i));
        var token = await ViewerTokenAsync();
        var query = $"?product_id={product.ProductId}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}&limit=3";

        var page1 = await CardAsync(token, query);
        var page2 = await CardAsync(token, query + "&offset=3");
        var page3 = await CardAsync(token, query + "&offset=6");

        Assert.Equal(7, page1.GetProperty("total_rows").GetInt32());
        var balances = new[] { page1, page2, page3 }.SelectMany(p => p.GetProperty("rows").EnumerateArray()).Select(r => r.Dec("balance")).ToList();
        Assert.Equal([5m, 8m, 6m, 10m, 9m, 15m, 12m], balances);
        Assert.Equal(12m, page1.Dec("closing_balance"));                             // totals cover the whole range on every page
    }

    [DatabaseFact]
    public async Task Location_filter_and_branch_date_boundaries()
    {
        var product = await db.CreateSellableProductAsync(1000m);
        // 10 Nov 18:00 UTC = 11 Nov 01:00 WIB
        await MoveAsync(product.ProductId, 8m, new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc), db.LocationB);
        await MoveAsync(product.ProductId, 5m, new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc), db.LocationA);
        var token = await ViewerTokenAsync();

        var on11 = await CardAsync(token, $"?product_id={product.ProductId}&location_id={db.LocationB}&from=2026-11-11&to=2026-11-11");
        var on10 = await CardAsync(token, $"?product_id={product.ProductId}&location_id={db.LocationB}&from=2026-11-10&to=2026-11-10");

        var row = Assert.Single(on11.GetProperty("rows").EnumerateArray());
        Assert.Equal(("STORAGE", 8m), (row.Str("location_code"), row.Dec("balance")));
        Assert.Empty(on10.GetProperty("rows").EnumerateArray());
        Assert.Equal(8m, await CurrentStockAsync(product.ProductId, db.LocationB));
    }

    [DatabaseFact]
    public async Task Errors_and_permission()
    {
        var product = await db.CreateSellableProductAsync(1000m);
        var token = await ViewerTokenAsync();

        var (reversed, reversedBody) = await _client.GetAsync($"/api/v1/stock/card?product_id={product.ProductId}&from=2026-11-10&to=2026-11-01", token);
        var (unknown, unknownBody) = await _client.GetAsync($"/api/v1/stock/card?product_id={Guid.CreateVersion7()}", token);
        var (cashier, _) = await _client.GetAsync($"/api/v1/stock/card?product_id={product.ProductId}", db.CashierToken());
        var (defaults, defaultsBody) = await _client.GetAsync($"/api/v1/stock/card?product_id={product.ProductId}", token);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_range"), (reversed, reversedBody.Str("code")));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_product"), (unknown, unknownBody.Str("code")));
        Assert.Equal(HttpStatusCode.Forbidden, cashier);
        Assert.Equal(HttpStatusCode.OK, defaults);
        Assert.Equal(new DateOnly(Today.Year, Today.Month, 1).ToString("yyyy-MM-dd"), defaultsBody.Str("from"));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), defaultsBody.Str("to"));
    }
}
