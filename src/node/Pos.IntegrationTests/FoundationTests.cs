using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;
using Pos.NodeService.Dev;
using Pos.NodeService.Documents;
using Pos.NodeService.Nodes;
using Pos.NodeService.Sync;
using Pos.Shared.Documents;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class FoundationTests(DatabaseFixture db)
{
    [DatabaseFact]
    public async Task Server_is_supported_mysql()
    {
        var result = await ServerVersionCheck.CheckAsync(db.Factory);

        Assert.True(result.Supported, result.Reason);
        Assert.Equal("MySQL", result.Product);
    }

    [DatabaseFact]
    public async Task Node_identity_resolves_configured_code()
    {
        var node = await new NodeIdentity(db.Factory, "TK01").GetAsync();

        Assert.Equal(db.NodeId, node.Id);
        Assert.Equal(db.BranchId, node.BranchId);
        Assert.Equal("TK01", node.Code);
        Assert.Equal("Asia/Jakarta", node.TimeZone);
    }

    [DatabaseFact]
    public async Task Unknown_node_code_is_reported()
    {
        var ex = await Assert.ThrowsAsync<NodeNotFoundException>(() => new NodeIdentity(db.Factory, "XX99").GetAsync());
        Assert.Contains("XX99", ex.Message);
    }

    [DatabaseFact]
    public async Task Document_numbers_are_unique_and_gapless_under_parallel_use()
    {
        var runner = new TransactionRunner(db.Factory);
        var node = await new NodeIdentity(db.Factory, "TK01").GetAsync();
        const string type = "T901";   // own type code: a fresh sequence (the database is recreated every run)

        var numbers = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
            runner.RunAsync((c, tx, ct) => DocumentNumbering.NextAsync(c, tx, node, type, DateTime.UtcNow, ct), "number"))));

        var period = DocumentNumber.Period(DateTime.UtcNow, node.TimeZone);
        var expected = Enumerable.Range(1, 30).Select(n => DocumentNumber.Format("TK01", type, period, n));
        Assert.Equal(expected.Order(), numbers.Order());
    }

    [DatabaseFact]
    public async Task Rolled_back_number_is_reused()
    {
        var node = await new NodeIdentity(db.Factory, "TK01").GetAsync();
        const string type = "R901";
        var now = DateTime.UtcNow;

        await using (var connection = await db.Factory.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.EndsWith("-000001", await DocumentNumbering.NextAsync(connection, tx, node, type, now, default));
            await tx.RollbackAsync();
        }

        var runner = new TransactionRunner(db.Factory);
        var number = await runner.RunAsync((c, tx, ct) => DocumentNumbering.NextAsync(c, tx, node, type, now, ct), "number");
        Assert.EndsWith("-000001", number);
    }

    [DatabaseFact]
    public async Task Number_period_follows_branch_timezone()
    {
        var node = await new NodeIdentity(db.Factory, "TK01").GetAsync();
        const string type = "P901";
        var runner = new TransactionRunner(db.Factory);

        // 2026-11-30 17:30 UTC is already 2026-12-01 in Jakarta
        var number = await runner.RunAsync((c, tx, ct) =>
            DocumentNumbering.NextAsync(c, tx, node, type, new DateTime(2026, 11, 30, 17, 30, 0, DateTimeKind.Utc), ct), "number");

        Assert.Equal($"TK01-{type}-202612-000001", number);
    }

    [DatabaseFact]
    public async Task Outbox_row_is_written_in_the_document_transaction()
    {
        var docId = Guid.CreateVersion7();
        var number = "TEST-MT-" + docId.ToString("N")[..12];
        Task InsertTransfer(MySqlConnector.MySqlConnection c, MySqlConnector.MySqlTransaction tx) => c.ExecuteAsync(
            """
            INSERT INTO location_transfers (id, node_id, number, from_location_id, to_location_id, transferred_at, created_by)
            VALUES (@docId, @NodeId, @number, @StorageLocationId, @DisplayLocationId, '2026-11-20 03:00:00.000', @AdminId)
            """, new { docId, db.Seed.NodeId, number, db.Seed.StorageLocationId, db.Seed.DisplayLocationId, db.Seed.AdminId }, tx);

        await using (var connection = await db.Factory.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await InsertTransfer(connection, tx);
            await OutboxWriter.WriteAsync(connection, tx, SyncDocTypes.LocationTransfer, docId, 1, default);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await CountOutboxAsync(docId));

        var runner = new TransactionRunner(db.Factory);
        await runner.RunAsync(async (c, tx, ct) =>
        {
            await InsertTransfer(c, tx);
            return await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.LocationTransfer, docId, 1, ct);
        }, "outbox");

        await using var read = await db.Factory.OpenAsync();
        var row = await read.QuerySingleAsync<(string DocType, int RowVersion, string Payload, DateTime? SentAt)>(
            "SELECT doc_type, row_version, payload, sent_at FROM sync_outbox WHERE doc_id = @docId", new { docId });
        Assert.Equal(SyncDocTypes.LocationTransfer, row.DocType);
        Assert.Equal(1, row.RowVersion);
        Assert.Null(row.SentAt);

        // Format 2 (docs/sync-contract.md): the rows as stored.
        using var json = JsonDocument.Parse(row.Payload);
        Assert.Equal(2, json.RootElement.GetProperty("format").GetInt32());
        Assert.Equal(db.Seed.NodeId.ToString(), json.RootElement.GetProperty("node_id").GetString());
        var transfer = json.RootElement.GetProperty("tables")[0];
        Assert.Equal("location_transfers", transfer.GetProperty("table").GetString());
        var stored = transfer.GetProperty("rows")[0];
        Assert.Equal(number, stored.GetProperty("number").GetString());
        Assert.Equal("2026-11-20 03:00:00.000", stored.GetProperty("transferred_at").GetString());
    }

    [DatabaseFact]
    public async Task Dev_seed_creates_master_data_and_opening_stock()
    {
        await using var connection = await db.Factory.OpenAsync();

        Assert.Equal(5, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE sku IN @skus", new { skus = DevSeeder.ProductSkus }));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment_methods WHERE code IN ('CASH','QRIS','CREDIT')"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM locations WHERE node_id = @NodeId AND is_sellable = 1", new { db.NodeId }));
        Assert.True(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM prices") >= 5);

        // opening stock came from an ADJUSTMENT document with ledger rows and an outbox row
        var adjustmentId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM stock_adjustments WHERE node_id = @NodeId ORDER BY number LIMIT 1", new { db.NodeId });
        Assert.True(await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_ledger WHERE doc_type = 'ADJUSTMENT' AND doc_id = @adjustmentId", new { adjustmentId }) >= 5);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_type = @type AND doc_id = @adjustmentId", new { type = SyncDocTypes.StockAdjustment, adjustmentId }));
        Assert.Equal(0m, await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(b.base_qty), 0) - (SELECT SUM(base_qty) FROM stock_ledger WHERE doc_id = @adjustmentId)
            FROM stock_balances b JOIN products p ON p.id = b.product_id WHERE p.sku IN @skus
            """, new { adjustmentId, skus = DevSeeder.ProductSkus }));
    }

    [DatabaseFact]
    public async Task Dev_seed_twice_changes_nothing()
    {
        var seeder = new DevSeeder(new TransactionRunner(db.Factory), TimeProvider.System);

        var again = await seeder.SeedAsync("TK01");

        Assert.False(again.Created);
        Assert.Equal(db.Seed.NodeId, again.NodeId);
        Assert.Equal(db.Seed.Products.Select(p => p.ProductUnitId), again.Products.Select(p => p.ProductUnitId));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM nodes WHERE code = 'TK01'"));
    }

    private async Task<int> CountOutboxAsync(Guid docId)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox WHERE doc_id = @docId", new { docId });
    }
}
