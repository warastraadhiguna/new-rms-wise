using Dapper;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.NodeService.Stock;

/// <summary>One stock_ledger row on the Kartu Stok.</summary>
/// <param name="OccurredAt">UTC (shown in the branch timezone).</param>
/// <param name="DocLabel">Penjualan, Retur, Terima Barang, Mutasi, Penyesuaian, Opname.</param>
/// <param name="UserName">Who made the source document.</param>
/// <param name="Balance">Running balance after this row (opening balance + every row up to here).</param>
public sealed record StockCardRowDto(
    Guid LedgerId,
    DateTime OccurredAt,
    string DocType,
    string DocLabel,
    Guid DocId,
    string? DocNumber,
    string? UserName,
    Guid LocationId,
    string LocationCode,
    DateOnly? ExpiryDate,
    decimal QtyIn,
    decimal QtyOut,
    decimal Balance);

/// <summary>
/// Kartu Stok of one product (one location or all of this node's), branch dates <c>From</c>..<c>To</c> inclusive.
/// Totals cover the whole range; <c>Rows</c> is one page. No purchase price or cost (HQ data).
/// </summary>
/// <param name="OpeningBalance">Stock before <c>From</c> (sum of earlier ledger rows).</param>
/// <param name="ClosingBalance">Opening + every row in the range; equals the current stock when the range reaches today.</param>
public sealed record StockCardDto(
    Guid ProductId,
    string Sku,
    string Name,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    Guid? LocationId,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    decimal OpeningBalance,
    decimal TotalIn,
    decimal TotalOut,
    decimal ClosingBalance,
    int TotalRows,
    int Offset,
    int Limit,
    IReadOnlyList<StockCardRowDto> Rows);

/// <summary>GET /api/v1/stock/card (permission stock.view), paged: the ledger can hold thousands of rows per product.</summary>
public sealed class StockCardService(DbConnectionFactory connections, NodeIdentity nodeIdentity)
{
    public const int MaxPageSize = 1000;

    public async Task<StockCardDto> GetAsync(
        Guid productId, Guid? locationId, DateOnly from, DateOnly to, int offset, int limit, CancellationToken ct = default)
    {
        if (to < from)
            throw ApiException.Unprocessable("invalid_range", "The end date is before the start date.");
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 500 : limit, 1, MaxPageSize);

        var node = await nodeIdentity.GetAsync(ct);
        var (fromUtc, toUtc) = BranchClock.DayRangeUtc(from, to, TimeZoneInfo.FindSystemTimeZoneById(node.TimeZone));
        await using var c = await connections.OpenAsync(ct);

        var product = await c.QuerySingleOrDefaultAsync<ProductRow>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sku AS Sku, p.name AS Name, bu.code AS BaseUnitCode, bu.allow_decimal AS BaseAllowDecimal
            FROM products p JOIN units bu ON bu.id = p.base_unit_id WHERE p.id = @productId
            """, new { productId }, cancellationToken: ct))
            ?? throw ApiException.NotFound("unknown_product", $"Product {productId} does not exist.");

        var args = new { nodeId = node.Id, productId, locationId, fromUtc, toUtc, offset, limit };
        const string scope =
            """
            FROM stock_ledger sl
            JOIN locations l ON l.id = sl.location_id AND l.node_id = @nodeId
            WHERE sl.product_id = @productId AND (@locationId IS NULL OR sl.location_id = @locationId)
            """;

        var opening = await c.ExecuteScalarAsync<decimal>(new CommandDefinition(
            $"SELECT COALESCE(SUM(sl.base_qty), 0) {scope} AND sl.occurred_at < @fromUtc", args, cancellationToken: ct));
        var totals = await c.QuerySingleAsync<(long Rows, decimal QtyIn, decimal QtyOut)>(new CommandDefinition(
            $"""
            SELECT COUNT(*), COALESCE(SUM(GREATEST(sl.base_qty, 0)), 0), COALESCE(SUM(GREATEST(-sl.base_qty, 0)), 0)
            {scope} AND sl.occurred_at >= @fromUtc AND sl.occurred_at < @toUtc
            """, args, cancellationToken: ct));

        // The running balance is computed over the whole range before paging, so every page shows true balances.
        var rows = await c.QueryAsync<LedgerRow>(new CommandDefinition(
            $"""
            WITH ranged AS (
                SELECT sl.id, sl.occurred_at, sl.created_at, sl.doc_type, sl.doc_id, sl.location_id, l.code AS location_code,
                       sl.expiry_date, sl.base_qty,
                       SUM(sl.base_qty) OVER (ORDER BY sl.occurred_at, sl.created_at, sl.id) AS running
                {scope} AND sl.occurred_at >= @fromUtc AND sl.occurred_at < @toUtc
            )
            SELECT r.id AS LedgerId, r.occurred_at AS OccurredAt, r.doc_type AS DocType, r.doc_id AS DocId,
                   COALESCE(s.number, sr.number, gr.number, dn.number, lt.number, sa.number) AS DocNumber,
                   CAST(sa.stock_count_id IS NOT NULL AS SIGNED) AS FromStockCount,
                   u.full_name AS UserName, r.location_id AS LocationId, r.location_code AS LocationCode,
                   r.expiry_date AS ExpiryDate, r.base_qty AS BaseQty, r.running AS Running
            FROM ranged r
            LEFT JOIN sales s ON r.doc_type = 'SALE' AND s.id = r.doc_id
            LEFT JOIN sale_returns sr ON r.doc_type = 'SALE_RETURN' AND sr.id = r.doc_id
            LEFT JOIN goods_receipts gr ON r.doc_type = 'GOODS_RECEIPT' AND gr.id = r.doc_id
            LEFT JOIN delivery_notes dn ON r.doc_type = 'DELIVERY' AND dn.id = r.doc_id
            LEFT JOIN location_transfers lt ON r.doc_type = 'LOCATION_TRANSFER' AND lt.id = r.doc_id
            LEFT JOIN stock_adjustments sa ON r.doc_type = 'ADJUSTMENT' AND sa.id = r.doc_id
            LEFT JOIN users u ON u.id = COALESCE(s.created_by, sr.created_by, gr.created_by, dn.created_by, lt.created_by, sa.created_by)
            ORDER BY r.occurred_at, r.created_at, r.id
            LIMIT @limit OFFSET @offset
            """, args, cancellationToken: ct));

        var page = rows.Select(r => new StockCardRowDto(
            r.LedgerId, DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc), r.DocType,
            StockDocTypes.Label(r.DocType, r.FromStockCount == 1), r.DocId, r.DocNumber, r.UserName, r.LocationId,
            r.LocationCode, r.ExpiryDate is { } e ? DateOnly.FromDateTime(e) : null,
            Math.Max(r.BaseQty, 0m), Math.Max(-r.BaseQty, 0m), opening + r.Running)).ToList();

        return new StockCardDto(product.Id, product.Sku, product.Name, product.BaseUnitCode, product.BaseAllowDecimal,
            locationId, from, to, node.TimeZone, opening, totals.QtyIn, totals.QtyOut, opening + totals.QtyIn - totals.QtyOut,
            (int)totals.Rows, offset, limit, page);
    }

    private sealed record ProductRow(Guid Id, string Sku, string Name, string BaseUnitCode, bool BaseAllowDecimal);

    private sealed record LedgerRow(
        Guid LedgerId, DateTime OccurredAt, string DocType, Guid DocId, string? DocNumber, long FromStockCount, string? UserName,
        Guid LocationId, string LocationCode, DateTime? ExpiryDate, decimal BaseQty, decimal Running);
}
