using Dapper;
using MySqlConnector;
using Pos.Shared.Stock;

namespace Pos.NodeService.Stock;

/// <summary>Dapper access to stock_ledger / stock_balances. All methods run inside the caller's transaction.</summary>
public static class StockRepository
{
    /// <summary>
    /// Locks every balance row of one location + product for the rest of the transaction.
    /// The null bucket row is created (or touched) first with an upsert, which takes an exclusive lock even
    /// when the product has no stock rows yet, so concurrent postings of the same product queue up here.
    /// </summary>
    public static async Task<IReadOnlyList<StockBucket>> LockBucketsAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid locationId, Guid productId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO stock_balances (location_id, product_id, expiry_key, base_qty)
            VALUES (@locationId, @productId, @noExpiry, 0)
            ON DUPLICATE KEY UPDATE base_qty = base_qty
            """,
            new { locationId, productId, noExpiry = ToDateTime(StockExpiryKey.NoExpiry) },
            transaction, cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<BalanceRow>(new CommandDefinition(
            """
            SELECT expiry_key AS ExpiryKey, base_qty AS BaseQty
            FROM stock_balances
            WHERE location_id = @locationId AND product_id = @productId
            ORDER BY expiry_key
            FOR UPDATE
            """,
            new { locationId, productId }, transaction, cancellationToken: cancellationToken));

        return rows.Select(r => new StockBucket(StockExpiryKey.FromKey(DateOnly.FromDateTime(r.ExpiryKey)), r.BaseQty)).ToList();
    }

    public static Task<bool?> GetTrackExpiryAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid productId, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT track_expiry FROM products WHERE id = @productId",
            new { productId }, transaction, cancellationToken: cancellationToken));

    /// <summary>IANA timezone of the branch that owns the location.</summary>
    public static Task<string?> GetLocationTimeZoneAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid locationId, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT b.timezone
            FROM locations l
            JOIN nodes n ON n.id = l.node_id
            JOIN branches b ON b.id = n.branch_id
            WHERE l.id = @locationId
            """,
            new { locationId }, transaction, cancellationToken: cancellationToken));

    public static Task InsertLedgerAsync(
        MySqlConnection connection, MySqlTransaction transaction, IEnumerable<StockLedgerEntry> entries,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO stock_ledger
              (id, node_id, location_id, product_id, expiry_date, base_qty, doc_type, doc_id, doc_line_id, occurred_at)
            VALUES
              (@Id, @NodeId, @LocationId, @ProductId, @ExpiryDate, @BaseQty, @DocType, @DocId, @DocLineId, @OccurredAt)
            """,
            entries.Select(e => new
            {
                e.Id, e.NodeId, e.LocationId, e.ProductId,
                ExpiryDate = e.ExpiryDate is { } d ? ToDateTime(d) : (DateTime?)null,
                e.BaseQty, e.DocType, e.DocId, e.DocLineId, e.OccurredAt,
            }).ToList(),
            transaction, cancellationToken: cancellationToken));

    /// <summary>Adds the entries to stock_balances, one upsert per bucket (null expiry -> 9999-12-31).</summary>
    public static Task UpsertBalancesAsync(
        MySqlConnection connection, MySqlTransaction transaction, IEnumerable<StockLedgerEntry> entries,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO stock_balances (location_id, product_id, expiry_key, base_qty)
            VALUES (@LocationId, @ProductId, @ExpiryKey, @BaseQty) AS new
            ON DUPLICATE KEY UPDATE base_qty = stock_balances.base_qty + new.base_qty
            """,
            entries
                .GroupBy(e => (e.LocationId, e.ProductId, Key: StockExpiryKey.ToKey(e.ExpiryDate)))
                .Select(g => new
                {
                    g.Key.LocationId, g.Key.ProductId,
                    ExpiryKey = ToDateTime(g.Key.Key),
                    BaseQty = g.Sum(e => e.BaseQty),
                }).ToList(),
            transaction, cancellationToken: cancellationToken));

    private static DateTime ToDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private sealed record BalanceRow(DateTime ExpiryKey, decimal BaseQty);
}
