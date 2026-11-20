using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;
using Pos.NodeService.Stock;
using Pos.Shared.Auth;
using Pos.Shared.Stock;

namespace Pos.IntegrationTests;

/// <summary>
/// stock_balances vs SUM(stock_ledger): differences are reported (log, /health) and never repaired automatically;
/// the admin rebuild endpoint repairs them. Every test leaves the database consistent again.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StockIntegrityTests(DatabaseFixture db)
{
    private static readonly DateOnly Jan = new(2027, 1, 31);
    private static readonly DateOnly Mar = new(2027, 3, 31);

    private StockIntegrityService Service(ListLogger<StockIntegrityService>? logger = null, StockIntegrityStatus? status = null) =>
        new(new TransactionRunner(db.Factory), status ?? new StockIntegrityStatus(), TimeProvider.System,
            logger ?? new ListLogger<StockIntegrityService>());

    private async Task<string> AdminTokenAsync()
    {
        var username = "stockadmin" + Random.Shared.Next(100000, 999999);
        var userId = await db.CreateUserAsync(username, "secret", roles: [("[\"stock.rebuild\"]", (Guid?)null)]);
        return db.TokenFor(userId, username, [Permissions.StockRebuild]);
    }

    private async Task ExecAsync(string sql, object parameters)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<decimal?> BalanceAsync(Guid location, Guid product, DateOnly? expiry)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<decimal?>(
            "SELECT base_qty FROM stock_balances WHERE location_id = @location AND product_id = @product AND expiry_key = @key",
            new { location, product, key = StockExpiryKey.ToKey(expiry).ToDateTime(TimeOnly.MinValue) });
    }

    /// <summary>
    /// Three broken buckets: A's balance edited (10 → 7), B's March row deleted (ledger only),
    /// C has a balance row without any ledger row.
    /// </summary>
    private async Task<(TestProduct A, TestProduct B, Guid C)> BreakThreeBucketsAsync()
    {
        var a = await db.CreateSellableProductAsync(1000m, stock: [(null, 10m)], name: "Integrity A");
        var b = await db.CreateSellableProductAsync(1000m, trackExpiry: true, stock: [(Jan, 5m), (Mar, 4m)], name: "Integrity B");
        var c = await db.CreateProductAsync(trackExpiry: false, name: "Integrity C");

        await ExecAsync("UPDATE stock_balances SET base_qty = 7 WHERE product_id = @ProductId AND location_id = @LocationA",
            new { a.ProductId, db.LocationA });
        await ExecAsync("DELETE FROM stock_balances WHERE product_id = @ProductId AND expiry_key = @mar",
            new { b.ProductId, mar = Mar.ToDateTime(TimeOnly.MinValue) });
        await ExecAsync("INSERT INTO stock_balances (location_id, product_id, expiry_key, base_qty) VALUES (@LocationB, @c, '9999-12-31', 3)",
            new { db.LocationB, c });
        return (a, b, c);
    }

    [DatabaseFact]
    public async Task Normal_postings_leave_no_difference()
    {
        var product = await db.CreateSellableProductAsync(1000m, trackExpiry: true, stock: [(Jan, 5m), (null, 2m)]);

        var report = await Service().CheckAsync();

        Assert.DoesNotContain(report.Mismatches, m => m.ProductId == product.ProductId);
        Assert.True(report.BalanceRows > 0);
    }

    [DatabaseFact]
    public async Task Deliberate_differences_are_detected_logged_and_not_repaired()
    {
        var (a, b, c) = await BreakThreeBucketsAsync();
        try
        {
            var logger = new ListLogger<StockIntegrityService>();
            var status = new StockIntegrityStatus();

            var report = await Service(logger, status).CheckAsync();

            Assert.False(report.IsConsistent);
            var edited = Assert.Single(report.Mismatches, m => m.ProductId == a.ProductId);
            Assert.Equal(7m, edited.BalanceQty);
            Assert.Equal(10m, edited.LedgerQty);
            Assert.Equal(-3m, edited.Difference);
            Assert.True(edited.BalanceRowExists);
            Assert.Null(edited.ExpiryDate);
            Assert.Equal(db.LocationA, edited.LocationId);
            Assert.Equal("DISPLAY", edited.LocationCode);

            var deleted = Assert.Single(report.Mismatches, m => m.ProductId == b.ProductId);
            Assert.Equal((Mar, 0m, 4m, false), (deleted.ExpiryDate!.Value, deleted.BalanceQty, deleted.LedgerQty, deleted.BalanceRowExists));

            var orphan = Assert.Single(report.Mismatches, m => m.ProductId == c);
            Assert.Equal((db.LocationB, 3m, 0m), (orphan.LocationId, orphan.BalanceQty, orphan.LedgerQty));

            Assert.Same(report, status.LastReport);
            var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error).Message;
            Assert.Contains("differ from SUM(stock_ledger)", error);
            Assert.Contains("balance 7.0000 vs ledger 10.0000", error);
            Assert.Contains("balance row missing", error);

            // The check only reports.
            Assert.Equal(7m, await BalanceAsync(db.LocationA, a.ProductId, null));
            Assert.Null(await BalanceAsync(db.LocationA, b.ProductId, Mar));
        }
        finally
        {
            await Service().RebuildAsync(db.Seed.AdminId, "test-cleanup");
        }
    }

    [DatabaseFact]
    public async Task Health_shows_a_warning_and_rebuild_needs_the_stock_rebuild_permission_then_repairs()
    {
        var (a, b, c) = await BreakThreeBucketsAsync();
        try
        {
            await using var factory = new NodeServiceFactory(db);
            using var client = factory.CreateClient();
            var admin = await AdminTokenAsync();

            var (checkStatus, check) = await client.PostAsync("/api/v1/admin/stock/integrity-check", new { }, admin);
            Assert.Equal(HttpStatusCode.OK, checkStatus);
            Assert.True(check.GetProperty("mismatch_count").GetInt32() >= 3);

            var (healthStatus, health) = await ApiClient.GetAsync(client, "/health");
            Assert.Equal(HttpStatusCode.OK, healthStatus);                          // still serving
            Assert.Equal("warning", health.Str("status"));
            var stock = health.GetProperty("stock_integrity");
            Assert.Equal("warning", stock.Str("status"));
            var listed = stock.GetProperty("mismatches").EnumerateArray().ToList();
            var edited = Assert.Single(listed, m => m.Id("product_id") == a.ProductId);
            Assert.Equal(7m, edited.Dec("balance_qty"));
            Assert.Equal(10m, edited.Dec("ledger_qty"));
            Assert.Equal(-3m, edited.Dec("difference"));

            // Not repaired by the check, nor by anyone without stock.rebuild.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/admin/stock/rebuild-balances", new { })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/admin/stock/rebuild-balances", new { }, db.CashierToken())).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/admin/stock/integrity-check", new { }, db.CashierToken())).Status);
            Assert.Equal(7m, await BalanceAsync(db.LocationA, a.ProductId, null));

            var (rebuildStatus, rebuild) = await client.PostAsync("/api/v1/admin/stock/rebuild-balances", new { }, admin);

            Assert.Equal(HttpStatusCode.OK, rebuildStatus);
            Assert.True(rebuild.GetProperty("buckets_fixed").GetInt32() >= 3);
            Assert.Equal(0, rebuild.GetProperty("after").GetProperty("mismatch_count").GetInt32());
            Assert.Equal(10m, await BalanceAsync(db.LocationA, a.ProductId, null));
            Assert.Equal(4m, await BalanceAsync(db.LocationA, b.ProductId, Mar));
            Assert.Equal(5m, await BalanceAsync(db.LocationA, b.ProductId, Jan));
            Assert.Equal(0m, await BalanceAsync(db.LocationB, c, null));

            var (_, healthAfter) = await ApiClient.GetAsync(client, "/health");
            Assert.Equal("ok", healthAfter.Str("status"));
            Assert.Equal("ok", healthAfter.GetProperty("stock_integrity").Str("status"));
            Assert.Equal(0, healthAfter.GetProperty("stock_integrity").GetProperty("mismatch_count").GetInt32());
        }
        finally
        {
            await Service().RebuildAsync(db.Seed.AdminId, "test-cleanup");
        }
    }

    [DatabaseFact]
    public async Task Rebuild_while_sales_post_stock_ends_consistent()
    {
        var product = await db.CreateSellableProductAsync(1000m, stock: [(null, 100m)], name: "Integrity busy");
        await ExecAsync("UPDATE stock_balances SET base_qty = 55 WHERE product_id = @ProductId AND location_id = @LocationA",
            new { product.ProductId, db.LocationA });

        var posting = new StockPostingService(db.Factory);
        var postings = Enumerable.Range(0, 20).Select(_ => Task.Run(() => posting.PostAsync([
            new StockMovement(
                new StockDocumentLine(db.NodeId, db.LocationA, product.ProductId, StockDocTypes.Sale,
                    Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow),
                StockDirection.Out, 1m)]))).ToList();
        var rebuild = Task.Run(() => Service().RebuildAsync(db.Seed.AdminId, "test"));
        await Task.WhenAll(postings.Cast<Task>().Append(rebuild));

        var report = await Service().CheckAsync();

        Assert.DoesNotContain(report.Mismatches, m => m.ProductId == product.ProductId);
        Assert.Equal(80m, await BalanceAsync(db.LocationA, product.ProductId, null));
    }

    [DatabaseFact]
    public async Task Startup_check_fills_health()
    {
        await using var factory = new NodeServiceFactory(db);
        using var client = factory.CreateClient();

        JsonElement stock = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var (_, health) = await ApiClient.GetAsync(client, "/health");
            stock = health.GetProperty("stock_integrity");
            if (stock.Str("status") != "pending")
                break;
            await Task.Delay(100);
        }

        Assert.Equal("ok", stock.Str("status"));
        Assert.NotEqual(JsonValueKind.Null, stock.GetProperty("checked_at").ValueKind);
    }
}
