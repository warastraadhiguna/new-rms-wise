using Dapper;
using MySqlConnector;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;
using Pos.NodeService.Stock;
using Pos.Shared.Stock;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class StockPostingTests(DatabaseFixture db)
{
    private static readonly DateOnly E1 = new(2027, 1, 1);
    private static readonly DateOnly E2 = new(2027, 3, 1);
    private static readonly DateOnly NoExpiry = new(9999, 12, 31);

    private StockPostingService Service => new(db.Factory);

    private StockMovement Movement(Guid product, StockDirection direction, decimal qty, DateOnly? expiry = null,
        Guid? location = null, Guid? node = null, string docType = StockDocTypes.Adjustment) =>
        new(new StockDocumentLine(node ?? db.NodeId, location ?? db.LocationA, product, docType,
                Guid.CreateVersion7(), Guid.CreateVersion7(), DatabaseFixture.OccurredAt),
            direction, qty, expiry);

    private async Task<Dictionary<DateOnly, decimal>> BalancesAsync(Guid product, Guid? location = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        var rows = await connection.QueryAsync<(DateTime ExpiryKey, decimal BaseQty)>(
            "SELECT expiry_key, base_qty FROM stock_balances WHERE product_id = @product AND location_id = @location",
            new { product, location = location ?? db.LocationA });
        return rows.ToDictionary(r => DateOnly.FromDateTime(r.ExpiryKey), r => r.BaseQty);
    }

    private async Task<int> LedgerCountAsync(Guid product)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM stock_ledger WHERE product_id = @product", new { product });
    }

    [DatabaseFact]
    public async Task Posting_writes_ledger_and_balances()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);

        await Service.PostAsync([Movement(product, StockDirection.In, 10m, E1, docType: StockDocTypes.GoodsReceipt)]);
        var sale = Movement(product, StockDirection.Out, 4m, docType: StockDocTypes.Sale);
        var entries = await Service.PostAsync([sale]);

        var single = Assert.Single(entries);
        Assert.Equal(E1, single.ExpiryDate);
        Assert.Equal(-4m, single.BaseQty);

        await using var connection = await db.Factory.OpenAsync();
        var row = await connection.QuerySingleAsync<(Guid Id, Guid NodeId, Guid LocationId, DateTime ExpiryDate, decimal BaseQty, string DocType, Guid DocId, Guid DocLineId, DateTime OccurredAt)>(
            "SELECT id, node_id, location_id, expiry_date, base_qty, doc_type, doc_id, doc_line_id, occurred_at FROM stock_ledger WHERE doc_id = @DocId",
            new { sale.Line.DocId });
        Assert.Equal(single.Id, row.Id);
        Assert.Equal(7, row.Id.Version);
        Assert.Equal(db.NodeId, row.NodeId);
        Assert.Equal(db.LocationA, row.LocationId);
        Assert.Equal(E1, DateOnly.FromDateTime(row.ExpiryDate));
        Assert.Equal(-4m, row.BaseQty);
        Assert.Equal(StockDocTypes.Sale, row.DocType);
        Assert.Equal(sale.Line.DocLineId, row.DocLineId);
        Assert.Equal(DatabaseFixture.OccurredAt, row.OccurredAt);

        var balances = await BalancesAsync(product);
        Assert.Equal(6m, balances[E1]);
        Assert.Equal(0m, balances.GetValueOrDefault(NoExpiry));
    }

    [DatabaseFact]
    public async Task Shortage_and_null_expiry_use_the_sentinel_key()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);

        await Service.PostAsync([Movement(product, StockDirection.In, 2m, E1)]);
        var entries = await Service.PostAsync([Movement(product, StockDirection.Out, 5m)]);

        Assert.Equal([(E1, -2m), ((DateOnly?)null, -3m)], entries.Select(e => (e.ExpiryDate, e.BaseQty)));
        var balances = await BalancesAsync(product);
        Assert.Equal(0m, balances[E1]);
        Assert.Equal(-3m, balances[NoExpiry]);

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_ledger WHERE product_id = @product AND expiry_date IS NULL", new { product }));
    }

    [DatabaseFact]
    public async Task Several_lines_of_one_document_see_each_other()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 5m, E1)]);

        var entries = await Service.PostAsync(
            [Movement(product, StockDirection.Out, 3m), Movement(product, StockDirection.Out, 3m)]);

        Assert.Equal([(E1, -3m), (E1, -2m), ((DateOnly?)null, -1m)], entries.Select(e => ((DateOnly?)e.ExpiryDate, e.BaseQty)));
    }

    [DatabaseFact]
    public async Task Balance_equals_ledger_sum_per_bucket()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);

        await Service.PostAsync([Movement(product, StockDirection.Out, 4m)]);
        await Service.PostAsync([Movement(product, StockDirection.In, 10m, E2), Movement(product, StockDirection.In, 3m, E1)]);
        await Service.PostAsync([Movement(product, StockDirection.Out, 7.5m)]);
        await Service.TransferAsync(new StockTransferRequest(db.NodeId, product, db.LocationA, db.LocationB, 2m,
            Guid.CreateVersion7(), null, DatabaseFixture.OccurredAt));
        await Service.PostAsync([Movement(product, StockDirection.Out, 1m, location: db.LocationB)]);

        await using var connection = await db.Factory.OpenAsync();
        var mismatches = await connection.QueryAsync(
            """
            SELECT b.location_id, b.expiry_key, b.base_qty, COALESCE(l.total, 0) AS ledger_total
            FROM stock_balances b
            LEFT JOIN (
              SELECT location_id, COALESCE(expiry_date, '9999-12-31') AS expiry_key, SUM(base_qty) AS total
              FROM stock_ledger WHERE product_id = @product
              GROUP BY location_id, COALESCE(expiry_date, '9999-12-31')
            ) l ON l.location_id = b.location_id AND l.expiry_key = b.expiry_key
            WHERE b.product_id = @product AND b.base_qty <> COALESCE(l.total, 0)
            UNION ALL
            SELECT l.location_id, l.expiry_key, NULL, l.total
            FROM (
              SELECT location_id, COALESCE(expiry_date, '9999-12-31') AS expiry_key, SUM(base_qty) AS total
              FROM stock_ledger WHERE product_id = @product
              GROUP BY location_id, COALESCE(expiry_date, '9999-12-31')
            ) l
            LEFT JOIN stock_balances b
              ON b.product_id = @product AND b.location_id = l.location_id AND b.expiry_key = l.expiry_key
            WHERE b.location_id IS NULL
            """, new { product });
        Assert.Empty(mismatches);

        // and the totals line up with what was posted: -4 +13 -7.5 -1 (transfer is neutral)
        Assert.Equal(0.5m, await connection.ExecuteScalarAsync<decimal>(
            "SELECT SUM(base_qty) FROM stock_balances WHERE product_id = @product", new { product }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_balances WHERE product_id = @product AND expiry_key <> '9999-12-31' AND base_qty < 0", new { product }));
    }

    [DatabaseFact]
    public async Task Transfer_moves_stock_with_expiry_between_locations()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 5m, E1)]);

        var result = await Service.TransferAsync(new StockTransferRequest(db.NodeId, product, db.LocationA, db.LocationB, 3m,
            Guid.CreateVersion7(), null, DatabaseFixture.OccurredAt));

        Assert.Equal(3m, result.In.Sum(e => e.BaseQty));
        Assert.Equal(2m, (await BalancesAsync(product, db.LocationA))[E1]);
        Assert.Equal(3m, (await BalancesAsync(product, db.LocationB))[E1]);
    }

    [DatabaseFact]
    public async Task Failed_insert_rolls_back_the_whole_transaction()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 10m, E1)]);
        var ledgerBefore = await LedgerCountAsync(product);
        var balancesBefore = await BalancesAsync(product);

        // first line is valid; the second references a node that does not exist -> FK error on its ledger insert
        await Assert.ThrowsAnyAsync<MySqlException>(() => Service.PostAsync(
        [
            Movement(product, StockDirection.Out, 4m),
            Movement(product, StockDirection.Out, 1m, node: Guid.CreateVersion7()),
        ]));

        Assert.Equal(ledgerBefore, await LedgerCountAsync(product));
        Assert.Equal(balancesBefore, await BalancesAsync(product));
    }

    [DatabaseFact]
    public async Task Unknown_product_is_rejected_before_writing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PostAsync([Movement(Guid.CreateVersion7(), StockDirection.Out, 1m)]));
    }

    [DatabaseFact]
    public async Task Twenty_parallel_sales_of_the_same_product_end_with_correct_balance()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 50m, E1)]);

        // 20 cashiers sell 3 each at the same time: 60 > 50, so 10 must land on the null bucket
        var sales = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => new StockPostingService(db.Factory)
                .PostAsync([Movement(product, StockDirection.Out, 3m, docType: StockDocTypes.Sale)])))
            .ToArray();
        await Task.WhenAll(sales);

        var balances = await BalancesAsync(product);
        Assert.Equal(0m, balances[E1]);
        Assert.Equal(-10m, balances[NoExpiry]);

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(-10m, await connection.ExecuteScalarAsync<decimal>(
            "SELECT SUM(base_qty) FROM stock_ledger WHERE product_id = @product", new { product }));
        Assert.Equal(-50m, await connection.ExecuteScalarAsync<decimal>(
            "SELECT SUM(base_qty) FROM stock_ledger WHERE product_id = @product AND expiry_date = '2027-01-01' AND base_qty < 0", new { product }));
    }

    /// <summary>
    /// Another transaction holds the product's balance rows; the service's connection gives up waiting after
    /// 1 s (error 1205) and the whole transaction is retried until the blocker commits.
    /// </summary>
    [DatabaseFact]
    public async Task Lock_wait_timeout_is_retried_and_then_succeeds()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 10m, E1)]);

        var log = new ListLogger<StockPostingService>();
        var impatient = new StockPostingService(new DbConnectionFactory(db.ConnectionString, lockWaitTimeoutSeconds: 1), log);

        await using (var blocker = await db.Factory.OpenAsync())
        {
            await using var tx = await blocker.BeginTransactionAsync();
            await blocker.ExecuteAsync(
                "SELECT * FROM stock_balances WHERE product_id = @product FOR UPDATE", new { product }, tx);

            var sale = impatient.PostAsync([Movement(product, StockDirection.Out, 4m)]);
            await Task.Delay(TimeSpan.FromSeconds(1.6));
            Assert.False(sale.IsCompleted, "sale must wait for the blocker");
            await tx.CommitAsync();

            await sale;
        }

        Assert.NotEmpty(log.Warnings);
        Assert.All(log.Warnings, w => Assert.Contains("1205", w));
        Assert.Equal(6m, (await BalancesAsync(product))[E1]);
    }

    [DatabaseFact]
    public async Task Lock_wait_timeout_gives_up_after_three_retries_without_writing()
    {
        var product = await db.CreateProductAsync(trackExpiry: true);
        await Service.PostAsync([Movement(product, StockDirection.In, 10m, E1)]);
        var ledgerBefore = await LedgerCountAsync(product);

        var log = new ListLogger<StockPostingService>();
        var impatient = new StockPostingService(new DbConnectionFactory(db.ConnectionString, lockWaitTimeoutSeconds: 1), log);

        await using (var blocker = await db.Factory.OpenAsync())
        {
            await using var tx = await blocker.BeginTransactionAsync();
            await blocker.ExecuteAsync(
                "SELECT * FROM stock_balances WHERE product_id = @product FOR UPDATE", new { product }, tx);

            var ex = await Assert.ThrowsAsync<MySqlException>(() => impatient.PostAsync([Movement(product, StockDirection.Out, 4m)]));
            Assert.Equal(MySqlErrorCode.LockWaitTimeout, ex.ErrorCode);
            await tx.RollbackAsync();
        }

        Assert.Equal(3, log.Warnings.Count);
        Assert.Equal(ledgerBefore, await LedgerCountAsync(product));
        Assert.Equal(10m, (await BalancesAsync(product))[E1]);
    }

    [DatabaseFact]
    public async Task Created_at_is_stored_in_utc()
    {
        var product = await db.CreateProductAsync(trackExpiry: false);
        var before = DateTime.UtcNow.AddSeconds(-5);

        await Service.PostAsync([Movement(product, StockDirection.In, 1m)]);

        var after = DateTime.UtcNow.AddSeconds(5);
        await using var connection = await db.Factory.OpenAsync();
        var createdAt = await connection.ExecuteScalarAsync<DateTime>(
            "SELECT created_at FROM stock_ledger WHERE product_id = @product", new { product });
        var updatedAt = await connection.ExecuteScalarAsync<DateTime>(
            "SELECT updated_at FROM stock_balances WHERE product_id = @product", new { product });

        Assert.InRange(createdAt, before, after);
        Assert.InRange(updatedAt, before, after);
    }
}
