using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Stock;
using Pos.Shared.Auth;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.IntegrationTests;

/// <summary>Form Mutasi Lokasi endpoints (stock.transfer): FEFO per expiry, totals unchanged, idempotent, permissions.</summary>
[Collection(DatabaseCollection.Name)]
public class LocationTransferApiTests(DatabaseFixture db) : IAsyncLifetime
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

    /// <summary>A user of its own with exactly these permissions (role at this node).</summary>
    private async Task<(Guid Id, string Token)> UserAsync(params string[] permissions)
    {
        var name = "mt" + Guid.NewGuid().ToString("N")[..12];
        var id = await db.CreateUserAsync(name, "secret-1", roles: [(JsonSerializer.Serialize(permissions), (Guid?)db.NodeId)]);
        return (id, db.TokenFor(id, name, permissions));
    }

    private Task<(Guid Id, string Token)> TransferStaffAsync() => UserAsync(Permissions.StockView, Permissions.StockTransfer);

    /// <summary>Stock in STORAGE per expiry (the product tracks expiry).</summary>
    private async Task<TestProduct> StorageProductAsync(params (DateOnly? Expiry, decimal Qty)[] stock)
    {
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true);
        await new StockPostingService(db.Factory).PostAsync(stock.Select(s => new StockMovement(
            new StockDocumentLine(db.NodeId, db.LocationB, product.ProductId, StockDocTypes.Adjustment, Guid.CreateVersion7(),
                Guid.CreateVersion7(), DateTime.UtcNow), StockDirection.In, s.Qty, s.Expiry)).ToList());
        return product;
    }

    private async Task<Dictionary<(Guid Location, DateOnly? Expiry), decimal>> BucketsAsync(Guid productId)
    {
        await using var connection = await db.Factory.OpenAsync();
        var rows = await connection.QueryAsync<(Guid Location, DateTime Key, decimal Qty)>(
            "SELECT location_id, expiry_key, base_qty FROM stock_balances WHERE product_id = @productId AND base_qty <> 0", new { productId });
        return rows.ToDictionary(r => (r.Location, StockExpiryKey.FromKey(DateOnly.FromDateTime(r.Key))), r => r.Qty);
    }

    private static object Body(Guid id, Guid from, Guid to, params (Guid ProductUnitId, decimal Qty)[] lines) =>
        new { Id = id, FromLocationId = from, ToLocationId = to, Lines = lines.Select(l => new { l.ProductUnitId, l.Qty }).ToArray(), Notes = "isi etalase" };

    [DatabaseFact]
    public async Task Transfer_moves_fefo_per_expiry_and_keeps_the_node_total()
    {
        var e1 = Today.AddDays(40);
        var e2 = Today.AddDays(200);
        var product = await StorageProductAsync((e1, 5m), (e2, 10m));
        var (userId, token) = await TransferStaffAsync();
        var id = Guid.CreateVersion7();

        var (status, body) = await _client.PostAsync("/api/v1/stock/transfers", Body(id, db.LocationB, db.LocationA, (product.ProductUnitId, 8m)), token);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.StartsWith("TK01-MT-", body.Str("number"));
        Assert.Equal(("STORAGE", "DISPLAY", "isi etalase"), (body.Str("from_location_code"), body.Str("to_location_code"), body.Str("notes")));
        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal((8m, 8m, "PCS"), (line.Dec("qty"), line.Dec("base_qty"), line.Str("unit_code")));
        Assert.Equal([(e1.ToString("yyyy-MM-dd"), 5m), (e2.ToString("yyyy-MM-dd"), 3m)],
            line.GetProperty("expiries").EnumerateArray().Select(e => (e.Str("expiry_date"), e.Dec("base_qty"))));

        var buckets = await BucketsAsync(product.ProductId);
        Assert.False(buckets.ContainsKey((db.LocationB, e1)));                    // 5 - 5
        Assert.Equal(7m, buckets[(db.LocationB, e2)]);                             // 10 - 3
        Assert.Equal(5m, buckets[(db.LocationA, e1)]);
        Assert.Equal(3m, buckets[(db.LocationA, e2)]);
        Assert.Equal(15m, buckets.Values.Sum());                                   // nothing created or lost

        await using var connection = await db.Factory.OpenAsync();
        var ledger = (await connection.QueryAsync<(string DocType, Guid? DocLineId, decimal Qty)>(
            "SELECT doc_type, doc_line_id, base_qty FROM stock_ledger WHERE doc_id = @id", new { id })).ToList();
        var lineIds = (await connection.QueryAsync<Guid>("SELECT id FROM location_transfer_lines WHERE location_transfer_id = @id", new { id })).ToList();
        Assert.Equal(2, lineIds.Count);                                            // one row per expiry
        Assert.All(ledger, l => Assert.Equal("LOCATION_TRANSFER", l.DocType));
        Assert.All(ledger, l => Assert.Contains(l.DocLineId!.Value, lineIds));
        Assert.Equal(0m, ledger.Sum(l => l.Qty));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_type = 'LOCATION_TRANSFER' AND doc_id = @id", new { id }));
        Assert.Equal(userId, await connection.ExecuteScalarAsync<Guid>("SELECT created_by FROM location_transfers WHERE id = @id", new { id }));
    }

    [DatabaseFact]
    public async Task Units_are_converted_and_kept_for_the_detail()
    {
        var product = await StorageProductAsync((Today.AddDays(90), 60m));
        var boxUnit = Guid.CreateVersion7();
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO product_units (id, product_id, unit_id, conversion) SELECT @boxUnit, @ProductId, id, 24 FROM units WHERE code = 'BOX'",
                new { boxUnit, product.ProductId });
        }
        var (_, token) = await TransferStaffAsync();

        var (_, body) = await _client.PostAsync("/api/v1/stock/transfers",
            Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (boxUnit, 2m), (product.ProductUnitId, 3m)), token);

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([(1, "BOX", 2m, 48m), (2, "PCS", 3m, 3m)],
            lines.Select(l => (l.GetProperty("line_no").GetInt32(), l.Str("unit_code"), l.Dec("qty"), l.Dec("base_qty"))));
        Assert.Equal(51m, (await BucketsAsync(product.ProductId))[(db.LocationA, Today.AddDays(90))]);
    }

    [DatabaseFact]
    public async Task Sending_the_same_id_again_returns_the_stored_transfer_and_moves_once()
    {
        var product = await StorageProductAsync((null, 10m));
        var (_, token) = await TransferStaffAsync();
        var id = Guid.CreateVersion7();
        var body = Body(id, db.LocationB, db.LocationA, (product.ProductUnitId, 4m));

        var (first, firstBody) = await _client.PostAsync("/api/v1/stock/transfers", body, token);
        var (second, secondBody) = await _client.PostAsync("/api/v1/stock/transfers", body, token);

        Assert.Equal(HttpStatusCode.Created, first);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal(firstBody.Str("number"), secondBody.Str("number"));
        var buckets = await BucketsAsync(product.ProductId);
        Assert.Equal((6m, 4m), (buckets[(db.LocationB, null)], buckets[(db.LocationA, null)]));
    }

    [DatabaseFact]
    public async Task Shortage_is_allowed_and_the_source_goes_negative()
    {
        var product = await StorageProductAsync((Today.AddDays(30), 2m));
        var (_, token) = await TransferStaffAsync();

        var (status, _) = await _client.PostAsync("/api/v1/stock/transfers",
            Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (product.ProductUnitId, 5m)), token);

        Assert.Equal(HttpStatusCode.Created, status);
        var buckets = await BucketsAsync(product.ProductId);
        Assert.Equal(-3m, buckets[(db.LocationB, null)]);
        Assert.Equal(2m, buckets[(db.LocationA, Today.AddDays(30))]);
        Assert.Equal(3m, buckets[(db.LocationA, null)]);
        Assert.Equal(2m, buckets.Values.Sum());
    }

    [DatabaseFact]
    public async Task Preview_shows_fefo_allocation_stock_and_shortage_without_writing()
    {
        var product = await StorageProductAsync((Today.AddDays(-2), 4m), (Today.AddDays(20), 3m), (Today.AddDays(100), 5m));
        var (_, token) = await TransferStaffAsync();
        var before = await BucketsAsync(product.ProductId);

        var (status, body) = await _client.PostAsync("/api/v1/stock/transfers/preview", new
        {
            FromLocationId = db.LocationB,
            Lines = new[] { new { product.ProductUnitId, Qty = 6m }, new { product.ProductUnitId, Qty = 4m } },
        }, token);

        Assert.Equal(HttpStatusCode.OK, status);
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((12m, 8m, 0m), (lines[0].Dec("stock_at_source"), lines[0].Dec("available"), lines[0].Dec("shortage")));
        Assert.Equal([(Today.AddDays(20).ToString("yyyy-MM-dd"), 3m), (Today.AddDays(100).ToString("yyyy-MM-dd"), 3m)],
            lines[0].GetProperty("allocations").EnumerateArray().Select(a => (a.Str("expiry_date"), a.Dec("base_qty"))));   // expired skipped
        Assert.Equal((2m, 2m), (lines[1].Dec("available"), lines[1].Dec("shortage")));    // the second line sees the first
        Assert.Contains(lines[1].GetProperty("allocations").EnumerateArray(), a => a.GetProperty("expiry_date").ValueKind == JsonValueKind.Null);
        Assert.Equal(before, await BucketsAsync(product.ProductId));               // nothing written
    }

    [DatabaseFact]
    public async Task Bad_requests_are_refused()
    {
        var product = await StorageProductAsync((null, 1m));
        var (_, token) = await TransferStaffAsync();
        var otherNodeLocation = Guid.CreateVersion7();
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO locations (id, node_id, code, name, type, is_sellable) VALUES (@otherNodeLocation, @node, 'DISPLAY', 'Lain', 'DISPLAY', 1)",
                new { otherNodeLocation, node = await db.CreateOtherNodeAsync() });
        }

        async Task<string> Code(object body) => (await _client.PostAsync("/api/v1/stock/transfers", body, token)).Body.Str("code");

        Assert.Equal("same_location", await Code(Body(Guid.CreateVersion7(), db.LocationA, db.LocationA, (product.ProductUnitId, 1m))));
        Assert.Equal("unknown_location", await Code(Body(Guid.CreateVersion7(), db.LocationB, otherNodeLocation, (product.ProductUnitId, 1m))));
        Assert.Equal("no_lines", await Code(Body(Guid.CreateVersion7(), db.LocationB, db.LocationA)));
        Assert.Equal("invalid_quantity", await Code(Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (product.ProductUnitId, 1.5m))));
        Assert.Equal("invalid_id", await Code(Body(Guid.NewGuid(), db.LocationB, db.LocationA, (product.ProductUnitId, 1m))));
        Assert.Equal(1m, (await BucketsAsync(product.ProductId))[(db.LocationB, null)]);
    }

    [DatabaseFact]
    public async Task Permissions_for_transfer_list_and_product_search()
    {
        var product = await StorageProductAsync((null, 3m));
        var (_, viewer) = await UserAsync(Permissions.StockView);
        var (_, warehouse) = await UserAsync(Permissions.StockView, Permissions.StockTransfer);
        var body = Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (product.ProductUnitId, 1m));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/stock/transfers", body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/v1/stock/transfers", body, db.CashierToken())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/v1/stock/transfers", body, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/v1/stock/transfers/preview",
            new { FromLocationId = db.LocationB, Lines = Array.Empty<object>() }, viewer)).Status);
        var (created, transfer) = await _client.PostAsync("/api/v1/stock/transfers", body, warehouse);
        Assert.Equal(HttpStatusCode.Created, created);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/stock/transfers/{transfer.Id()}", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/stock/transfers", db.CashierToken())).Status);
        var (_, transferOnly) = await UserAsync(Permissions.StockTransfer);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/stock/locations", transferOnly)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/products/search?q={product.Barcode}", transferOnly)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/products/{product.ProductId}/units", transferOnly)).Status);
    }

    [DatabaseFact]
    public async Task List_by_period_detail_and_kartu_stok_show_the_transfer()
    {
        var product = await StorageProductAsync((null, 9m));
        var (_, token) = await TransferStaffAsync();
        var (_, created) = await _client.PostAsync("/api/v1/stock/transfers",
            Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (product.ProductUnitId, 4m)), token);
        var id = created.Id();

        var (_, list) = await _client.GetAsync($"/api/v1/stock/transfers?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", token);
        var (_, yesterday) = await _client.GetAsync($"/api/v1/stock/transfers?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}", token);
        var (_, detail) = await _client.GetAsync($"/api/v1/stock/transfers/{id}", token);
        var (unknown, unknownBody) = await _client.GetAsync($"/api/v1/stock/transfers/{Guid.CreateVersion7()}", token);
        var (_, card) = await _client.GetAsync($"/api/v1/stock/card?product_id={product.ProductId}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", token);

        var row = Assert.Single(list.EnumerateArray(), r => r.Id() == id);
        Assert.Equal((created.Str("number"), "STORAGE", "DISPLAY", 1), (row.Str("number"), row.Str("from_location_code"), row.Str("to_location_code"),
            row.GetProperty("line_count").GetInt32()));
        Assert.DoesNotContain(yesterday.EnumerateArray(), r => r.Id() == id);
        Assert.Equal(created.Str("number"), detail.Str("number"));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_transfer"), (unknown, unknownBody.Str("code")));
        var moves = card.GetProperty("rows").EnumerateArray().Where(r => r.Str("doc_type") == "LOCATION_TRANSFER").ToList();
        Assert.Equal(2, moves.Count);                                              // out of STORAGE, into DISPLAY
        Assert.All(moves, m => Assert.Equal(("Mutasi", created.Str("number")), (m.Str("doc_label"), m.Str("doc_number"))));
        Assert.All(moves, m => Assert.Equal(id, m.Id("doc_id")));
        Assert.Equal(9m, card.Dec("closing_balance"));                            // the node total did not change
    }

    // ---- owner decisions: DAMAGED takes expired first; Buat Mutasi Balik ---------------------------

    private static int _damaged;

    private async Task<Guid> DamagedLocationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO locations (id, node_id, code, name, type, is_sellable) VALUES (@id, @NodeId, @code, 'Barang rusak', 'DAMAGED', 0)",
            new { id, db.NodeId, code = $"RUSAK{Interlocked.Increment(ref _damaged):00}" });
        return id;
    }

    [DatabaseFact]
    public async Task Towards_a_damaged_location_expired_stock_goes_first_then_fefo()
    {
        var expired = Today.AddDays(-5);
        var near = Today.AddDays(10);
        var later = Today.AddDays(100);
        var product = await StorageProductAsync((expired, 3m), (near, 4m), (later, 5m));
        var damaged = await DamagedLocationAsync();
        var (_, token) = await TransferStaffAsync();

        var (_, preview) = await _client.PostAsync("/api/v1/stock/transfers/preview", new
        {
            FromLocationId = db.LocationB,
            ToLocationId = damaged,
            Lines = new[] { new { product.ProductUnitId, Qty = 5m } },
        }, token);
        var (_, display) = await _client.PostAsync("/api/v1/stock/transfers/preview", new
        {
            FromLocationId = db.LocationB,
            ToLocationId = db.LocationA,
            Lines = new[] { new { product.ProductUnitId, Qty = 5m } },
        }, token);
        var (status, saved) = await _client.PostAsync("/api/v1/stock/transfers",
            Body(Guid.CreateVersion7(), db.LocationB, damaged, (product.ProductUnitId, 5m)), token);

        static List<(string, decimal)> Allocations(JsonElement line, string property) =>
            line.GetProperty(property).EnumerateArray().Select(a => (a.Str("expiry_date"), a.Dec("base_qty"))).ToList();
        var damagedLine = preview.GetProperty("lines")[0];
        Assert.Equal([(expired.ToString("yyyy-MM-dd"), 3m), (near.ToString("yyyy-MM-dd"), 2m)], Allocations(damagedLine, "allocations"));
        Assert.Equal(12m, damagedLine.Dec("available"));                           // expired counts towards DAMAGED
        Assert.Equal([(near.ToString("yyyy-MM-dd"), 4m), (later.ToString("yyyy-MM-dd"), 1m)],
            Allocations(display.GetProperty("lines")[0], "allocations"));          // other destinations: expired stays
        Assert.Equal(9m, display.GetProperty("lines")[0].Dec("available"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal([(expired.ToString("yyyy-MM-dd"), 3m), (near.ToString("yyyy-MM-dd"), 2m)],
            Allocations(saved.GetProperty("lines")[0], "expiries"));
        var buckets = await BucketsAsync(product.ProductId);
        Assert.False(buckets.ContainsKey((db.LocationB, expired)));
        Assert.Equal(3m, buckets[(damaged, expired)]);
        Assert.Equal(12m, buckets.Values.Sum());
    }

    [DatabaseFact]
    public async Task A_transfer_can_be_reversed_once_in_the_opposite_direction()
    {
        var product = await StorageProductAsync((Today.AddDays(60), 10m));
        var (_, token) = await TransferStaffAsync();
        var (_, original) = await _client.PostAsync("/api/v1/stock/transfers",
            Body(Guid.CreateVersion7(), db.LocationB, db.LocationA, (product.ProductUnitId, 4m)), token);

        object Reverse(Guid from, Guid to) => new
        {
            Id = Guid.CreateVersion7(), FromLocationId = from, ToLocationId = to,
            Lines = new[] { new { product.ProductUnitId, Qty = 4m } }, ReversesId = original.Id(),
        };

        var (wrongWay, wrongWayBody) = await _client.PostAsync("/api/v1/stock/transfers", Reverse(db.LocationB, db.LocationA), token);
        var (status, reversal) = await _client.PostAsync("/api/v1/stock/transfers", Reverse(db.LocationA, db.LocationB), token);
        var (again, againBody) = await _client.PostAsync("/api/v1/stock/transfers", Reverse(db.LocationA, db.LocationB), token);
        var (_, originalNow) = await _client.GetAsync($"/api/v1/stock/transfers/{original.Id()}", token);
        var (unknown, unknownBody) = await _client.PostAsync("/api/v1/stock/transfers", new
        {
            Id = Guid.CreateVersion7(), FromLocationId = db.LocationA, ToLocationId = db.LocationB,
            Lines = new[] { new { product.ProductUnitId, Qty = 1m } }, ReversesId = Guid.CreateVersion7(),
        }, token);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "reversal_direction"), (wrongWay, wrongWayBody.Str("code")));
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal((original.Id(), original.Str("number")), (reversal.Id("reverses_id"), reversal.Str("reverses_number")));
        Assert.Equal((HttpStatusCode.Conflict, "already_reversed"), (again, againBody.Str("code")));
        Assert.Equal((reversal.Id(), reversal.Str("number")), (originalNow.Id("reversed_by_id"), originalNow.Str("reversed_by_number")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "unknown_transfer"), (unknown, unknownBody.Str("code")));
        var buckets = await BucketsAsync(product.ProductId);
        Assert.Equal(10m, buckets[(db.LocationB, Today.AddDays(60))]);            // back where it started
        Assert.False(buckets.ContainsKey((db.LocationA, Today.AddDays(60))));
    }

    [DatabaseFact]
    public async Task Seed_gives_stock_transfer_to_store_admin_and_warehouse_staff_only()
    {
        await using var connection = await db.Factory.OpenAsync();
        var roles = (await connection.QueryAsync<(string Code, string Permissions)>("SELECT code, CAST(permissions AS CHAR) FROM roles"))
            .ToDictionary(r => r.Code, r => PermissionSet.FromRoleJson([r.Permissions]));

        Assert.True(roles["STORE_ADMIN"].Has(Permissions.StockTransfer));
        Assert.True(roles["WAREHOUSE_STAFF"].Has(Permissions.StockTransfer));
        Assert.True(roles["WAREHOUSE_STAFF"].Has(Permissions.StockView));
        Assert.False(roles["WAREHOUSE_STAFF"].Has(Permissions.SaleCreate));
        Assert.False(roles["CASHIER"].Has(Permissions.StockTransfer));
    }
}
