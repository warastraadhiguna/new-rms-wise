using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Sync;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

/// <summary>
/// Tugas 13b: the Node Service sync agent against an HQ that answers with the real JSON contract (FakeHq on a local
/// port): master data from HQ lands at the node and takes effect at once, the node's documents reach HQ, a connection
/// lost in the middle of a batch is resent without duplicates, the node keeps selling without internet and catches up.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SyncEndToEndTests(DatabaseFixture db) : IAsyncLifetime
{
    private FakeHq _hq = null!;
    private NodeServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!TestDatabase.IsConfigured)
            return;
        // HQ's versions continue after what this node already has (earlier tests may have pulled).
        await using (var c = await db.Factory.OpenAsync())
            _hq = await FakeHq.StartAsync(long.TryParse(await c.ExecuteScalarAsync<string?>(
                "SELECT state_value FROM sync_state WHERE state_key = 'master.version'"), out var v) ? v : 0);
        _factory = new NodeServiceFactory(db, settings: new Dictionary<string, string?>
        {
            ["Sync:HqUrl"] = _hq.Url,
            ["Sync:PushBatchSize"] = "50",
        });
        new SyncTokenStore(Path.Combine(Path.GetDirectoryName(db.KeyPath)!, "sync-token.dat")).Save(FakeHq.Token);
        _client = _factory.CreateClient();
        await Agent.RunOnceAsync(CancellationToken.None);         // send what earlier tests left in the outbox
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (_hq is not null)
            await _hq.DisposeAsync();
    }

    private SyncAgent Agent => _factory.Services.GetRequiredService<SyncAgent>();

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await db.Factory.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("No value.");
    }

    /// <summary>A product as HQ sends it: unit, product, base unit, barcode and a global price.</summary>
    private (Guid Product, Guid ProductUnit, Guid Barcode, string Code) AddHqProduct(decimal price)
    {
        var code = "HQ" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var unit = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var productUnit = Guid.CreateVersion7();
        var barcode = Guid.CreateVersion7();
        _hq.AddChange("units", new JsonObject { ["id"] = unit.ToString(), ["code"] = code, ["name"] = "Satuan HQ", ["allow_decimal"] = 0, ["deleted_at"] = null });
        _hq.AddChange("products", new JsonObject
        {
            ["id"] = product.ToString(), ["sku"] = code, ["name"] = "Barang dari HQ " + code, ["base_unit_id"] = unit.ToString(),
            ["status"] = "ACTIVE", ["track_expiry"] = 0, ["allow_qty_multiplier"] = 1, ["deleted_at"] = null, ["a_column_only_hq_knows"] = "x",
        });
        _hq.AddChange("product_units", new JsonObject
        {
            ["id"] = productUnit.ToString(), ["product_id"] = product.ToString(), ["unit_id"] = unit.ToString(), ["conversion"] = "1.0000",
            ["is_default_sale"] = 1, ["is_default_purchase"] = 1, ["deleted_at"] = null,
        });
        _hq.AddChange("product_barcodes", new JsonObject { ["id"] = barcode.ToString(), ["product_unit_id"] = productUnit.ToString(), ["barcode"] = code, ["deleted_at"] = null });
        AddHqPrice(productUnit, price, DateTime.UtcNow.AddDays(-1));
        return (product, productUnit, barcode, code);
    }

    private void AddHqPrice(Guid productUnit, decimal price, DateTime effectiveFrom) =>
        _hq.AddChange("prices", new JsonObject
        {
            ["id"] = Guid.CreateVersion7().ToString(), ["product_unit_id"] = productUnit.ToString(), ["branch_id"] = null,
            ["price"] = price.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture), ["min_qty"] = "1.0000",
            ["effective_from"] = effectiveFrom.ToString("yyyy-MM-dd HH:mm:ss.fff"), ["created_by"] = db.Seed.AdminId.ToString(), ["deleted_at"] = null,
        });

    private async Task<decimal?> SearchPriceAsync(string code)
    {
        var (status, found) = await _client.GetAsync($"/api/v1/products/search?q={code}", db.CashierToken());
        Assert.Equal(HttpStatusCode.OK, status);
        return found.GetArrayLength() == 0 ? null : found[0].Dec("price");
    }

    private async Task<(Guid Device, Guid Shift)> OpenShiftAsync()
    {
        var device = await db.CreateDeviceAsync();
        var (status, shift) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (device, shift.Id());
    }

    private async Task<Guid> SellAsync(Guid device, Guid productUnit, decimal amount)
    {
        var id = Guid.CreateVersion7();
        var (status, _) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = id,
            Lines = new[] { new { ProductUnitId = productUnit, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = amount } },
        }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return id;
    }

    private Task<int> PendingAsync(params Guid[] docIds) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_id IN @docIds AND sent_at IS NULL", new { docIds });

    [DatabaseFact]
    public async Task Master_data_from_hq_reaches_the_node_and_takes_effect_without_a_restart()
    {
        var item = AddHqProduct(5000m);
        await Agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(5000m, await SearchPriceAsync(item.Code));
        Assert.Equal("ACTIVE", await ScalarAsync<string>("SELECT status FROM products WHERE id = @Product", new { item.Product }));

        // A new price from HQ: the next search uses it (same running service).
        AddHqPrice(item.ProductUnit, 6500m, DateTime.UtcNow.AddMinutes(-1));
        // A setting from HQ: the status bar limit changes.
        _hq.AddChange("settings", new JsonObject
        {
            ["id"] = Guid.CreateVersion7().ToString(), ["branch_id"] = db.BranchId.ToString(), ["setting_key"] = SyncStatusService.AlertMinutesSetting,
            ["setting_value"] = "45", ["deleted_at"] = null,
        });
        await Agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(6500m, await SearchPriceAsync(item.Code));
        var (_, status) = await _client.GetAsync("/api/v1/sync/status", db.CashierToken());
        Assert.Equal(45, status.GetProperty("alert_minutes").GetInt32());

        // HQ removes the barcode (soft delete): the node no longer finds the product by it.
        _hq.AddChange("product_barcodes", new JsonObject
        {
            ["id"] = item.Barcode.ToString(), ["product_unit_id"] = item.ProductUnit.ToString(), ["barcode"] = item.Code,
            ["deleted_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        });
        await Agent.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT deleted_at FROM product_barcodes WHERE id = @Barcode", new { item.Barcode }));

        // The cursor is HQ's last version; pulling again brings nothing new.
        Assert.Equal(await Agent.MasterVersionAsync(default), _hq.Heartbeats.Last()["master_version"]!.GetValue<long>());
    }

    [DatabaseFact]
    public async Task Pull_pages_are_applied_until_hq_has_nothing_newer()
    {
        _factory.Services.GetRequiredService<SyncOptions>().PullLimit = 2;
        var items = Enumerable.Range(0, 3).Select(_ => AddHqProduct(1000m)).ToList();    // 15 rows → 8 pages of 2

        await Agent.RunOnceAsync(CancellationToken.None);

        foreach (var item in items)
            Assert.Equal(1000m, await SearchPriceAsync(item.Code));
    }

    [DatabaseFact]
    public async Task Sales_of_the_node_reach_hq_with_their_rows_in_outbox_order()
    {
        var item = AddHqProduct(4000m);
        await Agent.RunOnceAsync(CancellationToken.None);
        var (device, shift) = await OpenShiftAsync();
        var sale = await SellAsync(device, item.ProductUnit, 4000m);

        await Agent.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await PendingAsync(shift, sale));
        var sales = _hq.Row(sale.ToString(), "sales");
        Assert.NotNull(sales);
        Assert.Equal("4000.0000", sales!["total"]!.GetValue<string>());
        Assert.Equal(db.Seed.NodeId.ToString(), _hq.Documents[sale.ToString()]["payload"]!["node_id"]!.GetValue<string>());
        Assert.Equal(device.ToString(), _hq.Row(shift.ToString(), "devices")!["id"]!.GetValue<string>());
        var answers = _hq.Answers.Select(a => a.DocId).ToList();
        Assert.True(answers.IndexOf(shift.ToString()) < answers.IndexOf(sale.ToString()), "the shift goes before its sale");
        Assert.NotEmpty(_hq.Heartbeats);
        Assert.Equal(0, _hq.Heartbeats.Last()["pending_outbox"]!.GetValue<int>());
    }

    [DatabaseFact]
    public async Task A_connection_lost_in_the_middle_of_a_batch_is_resent_without_duplicates()
    {
        var item = AddHqProduct(3000m);
        await Agent.RunOnceAsync(CancellationToken.None);
        var (device, shift) = await OpenShiftAsync();
        var sales = new[] { await SellAsync(device, item.ProductUnit, 3000m), await SellAsync(device, item.ProductUnit, 3000m) };

        _hq.DropPushAfterDocuments = 2;                              // HQ stores the shift and the first sale, then the line drops
        await Assert.ThrowsAsync<SyncUnavailableException>(() => Agent.RunOnceAsync(CancellationToken.None));
        Assert.Equal(3, await PendingAsync([shift, .. sales]));        // no answer came: nothing is marked as sent
        Assert.Equal("offline", _factory.Services.GetRequiredService<SyncStatus>().Current.State);

        await Agent.RunOnceAsync(CancellationToken.None);            // resend: HQ already has two of them

        Assert.Equal(0, await PendingAsync([shift, .. sales]));
        var answers = _hq.Answers.Where(a => a.DocId == shift.ToString() || sales.Select(s => s.ToString()).Contains(a.DocId)).ToList();
        Assert.Equal(["accepted", "accepted", "duplicate", "duplicate", "accepted"], answers.Select(a => a.Status));
        Assert.All(sales, s => Assert.True(_hq.Documents.ContainsKey(s.ToString())));
    }

    [DatabaseFact]
    public async Task Without_internet_the_node_keeps_selling_and_catches_up_later()
    {
        var item = AddHqProduct(2500m);
        await Agent.RunOnceAsync(CancellationToken.None);
        _hq.Offline = true;

        var (device, shift) = await OpenShiftAsync();                // selling needs no HQ
        var sale = await SellAsync(device, item.ProductUnit, 2500m);
        await Assert.ThrowsAsync<SyncUnavailableException>(() => Agent.RunOnceAsync(CancellationToken.None));
        AddHqPrice(item.ProductUnit, 2700m, DateTime.UtcNow.AddMinutes(-1));     // HQ changes a price meanwhile

        var (_, health) = await _client.GetAsync("/health", token: null);
        Assert.Equal("offline", health.GetProperty("sync").Str("state"));
        Assert.True(health.GetProperty("sync").GetProperty("pending_outbox").GetInt32() >= 2);

        _hq.Offline = false;
        await Agent.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await PendingAsync(shift, sale));
        Assert.True(_hq.Documents.ContainsKey(sale.ToString()));
        Assert.Equal(2700m, await SearchPriceAsync(item.Code));
        var (_, status) = await _client.GetAsync("/api/v1/sync/status", db.CashierToken());
        Assert.Equal("ok", status.Str("state"));
        Assert.StartsWith("Sinkron: baru saja · ", status.Str("text"));
        Assert.False(status.GetProperty("is_alert").GetBoolean());
    }

    [DatabaseFact]
    public async Task Refused_documents_wait_for_a_retry_or_are_parked_and_never_block_the_others()
    {
        var item = AddHqProduct(1500m);
        await Agent.RunOnceAsync(CancellationToken.None);
        var (device, shift) = await OpenShiftAsync();
        var later = await SellAsync(device, item.ProductUnit, 1500m);
        var never = await SellAsync(device, item.ProductUnit, 1500m);
        var fine = await SellAsync(device, item.ProductUnit, 1500m);
        _hq.Reject[later.ToString()] = ("missing_reference", true);
        _hq.Reject[never.ToString()] = ("foreign_node", false);

        await Agent.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await PendingAsync(shift, fine));
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT next_attempt_at FROM sync_outbox WHERE doc_id = @later", new { later }));
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT failed_at FROM sync_outbox WHERE doc_id = @never", new { never }));
        Assert.Contains("foreign_node", await ScalarAsync<string>("SELECT last_error FROM sync_outbox WHERE doc_id = @never", new { never }));

        // The retry waits: the next cycle does not send it again at once.
        var pushes = _hq.Answers.Count(a => a.DocId == later.ToString());
        await Agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(pushes, _hq.Answers.Count(a => a.DocId == later.ToString()));

        var (_, status) = await _client.GetAsync("/api/v1/sync/status", db.CashierToken());
        Assert.True(status.GetProperty("failed_outbox").GetInt32() >= 1);
        Assert.Contains("gagal", status.Str("text"));
        Assert.True(status.GetProperty("is_alert").GetBoolean());

        // Clean up for the other tests: the parked one is not pending anyway; let the retry through.
        _hq.Reject.Clear();
        await using var c = await db.Factory.OpenAsync();
        await c.ExecuteAsync("UPDATE sync_outbox SET next_attempt_at = NULL, failed_at = NULL WHERE doc_id IN @ids", new { ids = new[] { later, never } });
        await Agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, await PendingAsync(later, never));
    }

    [DatabaseFact]
    public async Task An_outbox_row_from_before_format_2_is_sent_as_a_snapshot()
    {
        var item = AddHqProduct(1000m);
        await Agent.RunOnceAsync(CancellationToken.None);
        var (device, _) = await OpenShiftAsync();
        var sale = await SellAsync(device, item.ProductUnit, 1000m);
        await using (var c = await db.Factory.OpenAsync())
            await c.ExecuteAsync("UPDATE sync_outbox SET payload = '{\"id\": \"old dto\", \"total\": \"1000.0000\"}' WHERE doc_id = @sale", new { sale });

        await Agent.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, _hq.Documents[sale.ToString()]["payload"]!["format"]!.GetValue<int>());
        Assert.Equal("1000.0000", _hq.Row(sale.ToString(), "sales")!["total"]!.GetValue<string>());
    }

    [DatabaseFact]
    public async Task A_revoked_token_stops_sync_and_says_so()
    {
        new SyncTokenStore(Path.Combine(Path.GetDirectoryName(db.KeyPath)!, "sync-token.dat")).Save("pos_this_token_was_revoked_at_hq");

        var error = await Assert.ThrowsAsync<SyncRefusedException>(() => Agent.RunOnceAsync(CancellationToken.None));

        Assert.Equal("invalid_token", error.Code);
        var (_, health) = await _client.GetAsync("/health", token: null);
        Assert.Equal("refused", health.GetProperty("sync").Str("state"));
    }

    [DatabaseFact]
    public async Task A_node_that_was_never_initialized_refuses_to_sell()
    {
        await using (var c = await db.Factory.OpenAsync())
            await c.ExecuteAsync("DELETE FROM sync_state WHERE state_key = @key", new { key = SyncState.Initialized });
        try
        {
            await using var factory = new NodeServiceFactory(db);
            using var client = factory.CreateClient();
            var device = await db.CreateDeviceAsync();
            var (status, problem) = await client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, db.CashierToken(), device);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("node_not_initialized", problem.Str("code"));

            var result = await factory.Services.GetRequiredService<NodeInitializer>().RunAsync(CancellationToken.None);
            Assert.False(result.Success);                                       // sync not configured in this host
        }
        finally
        {
            await using var c = await db.Factory.OpenAsync();
            await c.ExecuteAsync("INSERT IGNORE INTO sync_state (state_key, state_value) VALUES (@key, 'DEV_SEED')", new { key = SyncState.Initialized });
        }
    }

    [DatabaseFact]
    public async Task Init_from_hq_refuses_a_database_that_already_has_master_data()
    {
        await using (var c = await db.Factory.OpenAsync())
            await c.ExecuteAsync("DELETE FROM sync_state WHERE state_key = @key", new { key = SyncState.Initialized });
        try
        {
            var result = await _factory.Services.GetRequiredService<NodeInitializer>().RunAsync(CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("not empty", result.Message);
        }
        finally
        {
            await using var c = await db.Factory.OpenAsync();
            await c.ExecuteAsync("INSERT IGNORE INTO sync_state (state_key, state_value) VALUES (@key, 'DEV_SEED')", new { key = SyncState.Initialized });
        }
    }
}
