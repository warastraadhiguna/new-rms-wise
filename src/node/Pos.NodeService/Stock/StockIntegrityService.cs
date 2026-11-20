using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Pos.NodeService.Data;
using Pos.Shared.Stock;

namespace Pos.NodeService.Stock;

/// <summary>One balance bucket whose cached qty differs from the sum of its ledger rows.</summary>
/// <param name="ExpiryDate">null = the no-expiry bucket (expiry_key 9999-12-31).</param>
/// <param name="BalanceQty">stock_balances.base_qty (0 when the row is missing).</param>
/// <param name="LedgerQty">SUM(stock_ledger.base_qty) of the bucket.</param>
public sealed record StockMismatch(
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    DateOnly? ExpiryDate,
    decimal BalanceQty,
    decimal LedgerQty,
    bool BalanceRowExists)
{
    public decimal Difference => BalanceQty - LedgerQty;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{LocationCode ?? LocationId.ToString()}/{Sku ?? ProductId.ToString()}/{(ExpiryDate is { } d ? d.ToString("yyyy-MM-dd") : "no-expiry")}: " +
        $"balance {BalanceQty} vs ledger {LedgerQty}{(BalanceRowExists ? "" : " (balance row missing)")}");
}

/// <param name="MismatchCount">All differing buckets; <paramref name="Mismatches"/> holds at most <see cref="StockIntegrityService.MaxListed"/>.</param>
public sealed record StockIntegrityReport(
    DateTime CheckedAt,
    long BalanceRows,
    int MismatchCount,
    IReadOnlyList<StockMismatch> Mismatches,
    long DurationMs)
{
    public bool IsConsistent => MismatchCount == 0;
}

/// <summary>A bucket set to its ledger sum by a rebuild (values read under lock).</summary>
public sealed record StockBucketFix(
    Guid LocationId, Guid ProductId, DateOnly? ExpiryDate, decimal OldQty, decimal NewQty);

public sealed record StockRebuildResult(DateTime RebuiltAt, int BucketsFixed, IReadOnlyList<StockBucketFix> Fixes, StockIntegrityReport After);

/// <summary>Latest integrity result for /health. Written by the nightly check and by rebuilds.</summary>
public sealed class StockIntegrityStatus
{
    private volatile Snapshot _current = new(null, null, null);

    public StockIntegrityReport? LastReport => _current.Report;
    public string? LastError => _current.Error;
    public DateTime? LastErrorAt => _current.ErrorAt;

    public void Set(StockIntegrityReport report) => _current = new Snapshot(report, null, null);

    /// <summary>The check itself failed (e.g. database down at night); the previous report is kept.</summary>
    public void SetError(string error, DateTime at) => _current = _current with { Error = error, ErrorAt = at };

    private sealed record Snapshot(StockIntegrityReport? Report, string? Error, DateTime? ErrorAt);
}

/// <summary>
/// Compares <c>stock_balances</c> (cache) with <c>SUM(stock_ledger)</c> per bucket (location, product, expiry).
/// The check only reports; it never repairs. <see cref="RebuildAsync"/> (permission stock.rebuild) sets every
/// bucket of each differing location + product to its ledger sum.
/// </summary>
public sealed class StockIntegrityService(
    TransactionRunner runner, StockIntegrityStatus status, TimeProvider clock, ILogger<StockIntegrityService> logger)
{
    public const int MaxListed = 100;

    // One statement = one consistent snapshot in InnoDB, so postings running meanwhile never show up half-done.
    private const string MismatchSql =
        """
        SELECT x.location_id AS LocationId, l.code AS LocationCode, x.product_id AS ProductId, p.sku AS Sku,
               x.expiry_key AS ExpiryKey, SUM(x.balance_qty) AS BalanceQty, SUM(x.ledger_qty) AS LedgerQty,
               CAST(MAX(x.has_balance) AS SIGNED) AS HasBalance
        FROM (
            SELECT location_id, product_id, expiry_key, base_qty AS balance_qty, 0 AS ledger_qty, 1 AS has_balance
            FROM stock_balances
            UNION ALL
            SELECT location_id, product_id, COALESCE(expiry_date, DATE '9999-12-31'), 0, base_qty, 0
            FROM stock_ledger
        ) x
        LEFT JOIN locations l ON l.id = x.location_id
        LEFT JOIN products p ON p.id = x.product_id
        GROUP BY x.location_id, l.code, x.product_id, p.sku, x.expiry_key
        HAVING SUM(x.balance_qty) <> SUM(x.ledger_qty)
        ORDER BY x.location_id, x.product_id, x.expiry_key
        """;

    /// <summary>Runs the check, stores the result for /health and logs an error with the details when buckets differ.</summary>
    public async Task<StockIntegrityReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        List<StockMismatch> all;
        long balanceRows;
        await using (var connection = await runner.ConnectionFactory.OpenAsync(cancellationToken))
        {
            all = (await connection.QueryAsync<MismatchRow>(new CommandDefinition(MismatchSql, cancellationToken: cancellationToken)))
                .Select(r => r.ToMismatch()).ToList();
            balanceRows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM stock_balances", cancellationToken: cancellationToken));
        }

        var report = new StockIntegrityReport(
            clock.GetUtcNow().UtcDateTime, balanceRows, all.Count, all.Take(MaxListed).ToList(),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        status.Set(report);

        if (report.IsConsistent)
        {
            logger.LogInformation("Stock integrity check: {Rows} balance rows match stock_ledger ({Ms} ms).", balanceRows, report.DurationMs);
        }
        else
        {
            logger.LogError(
                "Stock integrity check: {Count} bucket(s) of stock_balances differ from SUM(stock_ledger). Not repaired automatically; " +
                "review, then POST /api/v1/admin/stock/rebuild-balances (permission stock.rebuild). Details{More}:{NewLine}{Details}",
                report.MismatchCount, report.MismatchCount > MaxListed ? $" (first {MaxListed})" : "", Environment.NewLine,
                string.Join(Environment.NewLine, report.Mismatches.Select(m => "  " + m)));
        }

        return report;
    }

    /// <summary>
    /// Sets every bucket of each differing location + product to its ledger sum. Each pair is locked exactly as a
    /// stock posting locks it (see <see cref="StockRepository.LockBucketsAsync"/>), so no posting can run in between.
    /// </summary>
    public async Task<StockRebuildResult> RebuildAsync(Guid userId, string? username, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(Guid LocationId, Guid ProductId)> pairs;
        await using (var connection = await runner.ConnectionFactory.OpenAsync(cancellationToken))
        {
            pairs = (await connection.QueryAsync<MismatchRow>(new CommandDefinition(MismatchSql, cancellationToken: cancellationToken)))
                .Select(r => (r.LocationId, r.ProductId)).Distinct().ToList();
        }

        var fixes = new List<StockBucketFix>();
        foreach (var (locationId, productId) in pairs)
        {
            var pairFixes = await runner.RunAsync(
                (connection, transaction, ct) => RebuildPairAsync(connection, transaction, locationId, productId, ct),
                "Stock balance rebuild", cancellationToken);
            fixes.AddRange(pairFixes);
        }

        foreach (var fix in fixes)
        {
            logger.LogWarning(
                "Stock balance rebuilt by {Username} ({UserId}): location {LocationId}, product {ProductId}, expiry {Expiry}: {Old} -> {New}.",
                username, userId, fix.LocationId, fix.ProductId, fix.ExpiryDate?.ToString("yyyy-MM-dd") ?? "none",
                fix.OldQty, fix.NewQty);
        }
        logger.LogWarning("Stock balance rebuild by {Username}: {Pairs} product/location pair(s), {Buckets} bucket(s) fixed.",
            username, pairs.Count, fixes.Count);

        var after = await CheckAsync(cancellationToken);
        return new StockRebuildResult(clock.GetUtcNow().UtcDateTime, fixes.Count, fixes, after);
    }

    private static async Task<IReadOnlyList<StockBucketFix>> RebuildPairAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid locationId, Guid productId, CancellationToken ct)
    {
        // Same lock as a posting: afterwards no ledger row of this pair can be added until we commit.
        var locked = await StockRepository.LockBucketsAsync(connection, transaction, locationId, productId, ct);
        var balances = locked.ToDictionary(b => StockExpiryKey.ToKey(b.ExpiryDate), b => b.BaseQty);

        var ledger = (await connection.QueryAsync<(DateTime ExpiryKey, decimal Qty)>(new CommandDefinition(
            """
            SELECT COALESCE(expiry_date, DATE '9999-12-31'), SUM(base_qty) FROM stock_ledger
            WHERE location_id = @locationId AND product_id = @productId
            GROUP BY COALESCE(expiry_date, DATE '9999-12-31')
            """,
            new { locationId, productId }, transaction, cancellationToken: ct)))
            .ToDictionary(r => DateOnly.FromDateTime(r.ExpiryKey), r => r.Qty);

        var fixes = new List<StockBucketFix>();
        foreach (var key in balances.Keys.Union(ledger.Keys).Order())
        {
            var hasRow = balances.TryGetValue(key, out var current);
            var expected = ledger.GetValueOrDefault(key);
            if (hasRow && current == expected)
                continue;

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO stock_balances (location_id, product_id, expiry_key, base_qty)
                VALUES (@locationId, @productId, @expiryKey, @expected) AS new
                ON DUPLICATE KEY UPDATE base_qty = new.base_qty
                """,
                new { locationId, productId, expiryKey = key.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), expected },
                transaction, cancellationToken: ct));
            fixes.Add(new StockBucketFix(locationId, productId, StockExpiryKey.FromKey(key), current, expected));
        }
        return fixes;
    }

    private sealed record MismatchRow(
        Guid LocationId, string? LocationCode, Guid ProductId, string? Sku, DateTime ExpiryKey,
        decimal BalanceQty, decimal LedgerQty, long HasBalance)
    {
        public StockMismatch ToMismatch() => new(
            LocationId, LocationCode, ProductId, Sku, StockExpiryKey.FromKey(DateOnly.FromDateTime(ExpiryKey)),
            BalanceQty, LedgerQty, HasBalance == 1);
    }
}
