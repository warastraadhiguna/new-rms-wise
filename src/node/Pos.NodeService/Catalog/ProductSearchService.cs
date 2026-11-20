using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared;
using Pos.Shared.Sales;

namespace Pos.NodeService.Catalog;

public sealed record PriceTierDto(decimal MinQty, decimal Price);

/// <summary>One sellable unit of a product. Stock is the sum over this node's sellable locations.</summary>
/// <param name="ShelfLocation">"Letak" on the cashier screen.</param>
/// <param name="Price">Effective price at this branch for quantity 1 (null = no price yet).</param>
/// <param name="DiscountPrice">"Harga Diskon": price after promotions; null until promotions exist (TODO).</param>
/// <param name="StockQty">Stock expressed in this unit (base qty / conversion, 4 decimals).</param>
/// <param name="MatchedBy">barcode | sku | name | unit</param>
/// <param name="BaseUnitCode">The product's base unit (stock is kept in it), e.g. PCS.</param>
/// <param name="BaseAllowDecimal"><c>units.allow_decimal</c> of the base unit (KG: true).</param>
public sealed record ProductSearchItem(
    Guid ProductId,
    Guid ProductUnitId,
    string Sku,
    string Name,
    string? Barcode,
    string UnitCode,
    string UnitName,
    decimal Conversion,
    bool AllowDecimal,
    bool AllowQtyMultiplier,
    bool TrackExpiry,
    string? Brand,
    string? Category,
    string? ShelfLocation,
    decimal? Price,
    decimal? DiscountPrice,
    IReadOnlyList<PriceTierDto> PriceTiers,
    decimal StockBaseQty,
    decimal StockQty,
    string MatchedBy,
    string BaseUnitCode,
    bool BaseAllowDecimal);

/// <summary>
/// Cashier product lookup: exact barcode, then exact SKU, then name (every word must occur, case-insensitive).
/// At most 50 rows. Indexed lookups for barcode/SKU; the name search scans product names, which stays fast
/// for a store-sized catalogue (tens of thousands of rows).
/// </summary>
public sealed class ProductSearchService(DbConnectionFactory connections, NodeIdentity nodeIdentity, TimeProvider clock)
{
    public const int MaxResults = 50;

    private const string Sellable = "p.deleted_at IS NULL AND p.status <> 'INACTIVE' AND pu.deleted_at IS NULL";

    public async Task<IReadOnlyList<ProductSearchItem>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            throw ApiException.Unprocessable("query_required", "Type a barcode, SKU or part of the product name.");

        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        // product_unit_id -> how it matched, in result order
        var matches = new List<Match>();

        var byBarcode = await c.QueryAsync<(Guid UnitId, string Barcode)>(new CommandDefinition(
            $"""
            SELECT pb.product_unit_id, pb.barcode FROM product_barcodes pb
            JOIN product_units pu ON pu.id = pb.product_unit_id
            JOIN products p ON p.id = pu.product_id
            WHERE pb.barcode = @q AND pb.deleted_at IS NULL AND {Sellable}
            """, new { q }, cancellationToken: ct));
        matches.AddRange(byBarcode.Select(m => new Match(m.UnitId, "barcode", m.Barcode)));

        var bySku = await c.QueryAsync<Guid>(new CommandDefinition(
            $"""
            SELECT pu.id FROM products p JOIN product_units pu ON pu.product_id = p.id
            WHERE p.sku = @q AND {Sellable} ORDER BY pu.sort_order, pu.conversion LIMIT {MaxResults}
            """, new { q }, cancellationToken: ct));
        matches.AddRange(bySku.Select(id => new Match(id, "sku", null)));

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        if (matches.Count < MaxResults)
        {
            var parameters = new DynamicParameters();
            var conditions = words.Select((w, i) =>
            {
                parameters.Add($"w{i}", "%" + EscapeLike(w) + "%");
                return $"p.name LIKE @w{i} ESCAPE '\\\\'";
            });
            var byName = await c.QueryAsync<Guid>(new CommandDefinition(
                $"""
                SELECT pu.id FROM products p JOIN product_units pu ON pu.product_id = p.id
                WHERE {Sellable} AND {string.Join(" AND ", conditions)}
                ORDER BY p.name, pu.sort_order, pu.conversion
                LIMIT {MaxResults}
                """, parameters, cancellationToken: ct));
            matches.AddRange(byName.Select(id => new Match(id, "name", null)));
        }

        return await BuildAsync(c, node, matches.DistinctBy(m => m.UnitId).Take(MaxResults).ToList(), ct);
    }

    /// <summary>Every sellable unit of one product (to change a cart line's unit), smallest unit first.</summary>
    public async Task<IReadOnlyList<ProductSearchItem>> GetUnitsAsync(Guid productId, CancellationToken ct = default)
    {
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        var unitIds = (await c.QueryAsync<Guid>(new CommandDefinition(
            $"""
            SELECT pu.id FROM products p JOIN product_units pu ON pu.product_id = p.id
            WHERE p.id = @productId AND {Sellable} ORDER BY pu.sort_order, pu.conversion
            """, new { productId }, cancellationToken: ct))).ToList();
        if (unitIds.Count == 0)
            throw ApiException.NotFound("unknown_product", $"Product {productId} does not exist or has no sellable unit.");

        return await BuildAsync(c, node, unitIds.Select(id => new Match(id, "unit", null)).ToList(), ct);
    }

    private async Task<IReadOnlyList<ProductSearchItem>> BuildAsync(
        MySqlConnection c, NodeInfo node, IReadOnlyList<Match> ordered, CancellationToken ct)
    {
        if (ordered.Count == 0)
            return [];
        var unitIds = ordered.Select(m => m.UnitId).ToList();

        var rows = (await c.QueryAsync<UnitRow>(new CommandDefinition(
            """
            SELECT pu.id AS ProductUnitId, p.id AS ProductId, p.sku AS Sku, p.name AS Name,
                   u.code AS UnitCode, u.name AS UnitName, pu.conversion AS Conversion, u.allow_decimal AS AllowDecimal,
                   p.allow_qty_multiplier AS AllowQtyMultiplier, p.track_expiry AS TrackExpiry,
                   b.name AS Brand, cat.name AS Category, p.shelf_location AS ShelfLocation,
                   bu.code AS BaseUnitCode, bu.allow_decimal AS BaseAllowDecimal,
                   (SELECT MIN(pb.barcode) FROM product_barcodes pb WHERE pb.product_unit_id = pu.id AND pb.deleted_at IS NULL) AS FirstBarcode
            FROM product_units pu
            JOIN products p ON p.id = pu.product_id
            JOIN units u ON u.id = pu.unit_id
            JOIN units bu ON bu.id = p.base_unit_id
            LEFT JOIN brands b ON b.id = p.brand_id
            LEFT JOIN categories cat ON cat.id = p.category_id
            WHERE pu.id IN @unitIds
            """, new { unitIds }, cancellationToken: ct))).ToDictionary(r => r.ProductUnitId);

        var prices = (await c.QueryAsync<(Guid UnitId, Guid? BranchId, decimal Price, decimal MinQty, DateTime EffectiveFrom)>(new CommandDefinition(
            """
            SELECT product_unit_id, branch_id, price, min_qty, effective_from FROM prices
            WHERE product_unit_id IN @unitIds AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            """, new { unitIds, branchId = node.BranchId }, cancellationToken: ct)))
            .ToLookup(p => p.UnitId, p => new PriceCandidate(p.BranchId, p.Price, p.MinQty, p.EffectiveFrom));

        var productIds = rows.Values.Select(r => r.ProductId).Distinct().ToList();
        var stock = (await c.QueryAsync<(Guid ProductId, decimal Qty)>(new CommandDefinition(
            """
            SELECT sb.product_id, SUM(sb.base_qty) FROM stock_balances sb
            JOIN locations l ON l.id = sb.location_id
            WHERE l.node_id = @nodeId AND l.is_sellable = 1 AND l.deleted_at IS NULL AND sb.product_id IN @productIds
            GROUP BY sb.product_id
            """, new { nodeId = node.Id, productIds }, cancellationToken: ct))).ToDictionary(s => s.ProductId, s => s.Qty);

        var now = clock.GetUtcNow().UtcDateTime;
        return ordered.Where(m => rows.ContainsKey(m.UnitId)).Select(m =>
        {
            var r = rows[m.UnitId];
            var candidates = prices[m.UnitId].ToList();
            var tiers = candidates.Where(p => p.EffectiveFrom <= now).Select(p => p.MinQty).Distinct().Order()
                .Select(minQty => (minQty, price: PriceResolver.Resolve(candidates, node.BranchId, now, minQty)))
                .Where(t => t.price is not null)
                .Select(t => new PriceTierDto(t.minQty, t.price!.Value))
                .DistinctBy(t => t.Price)
                .ToList();
            var baseQty = stock.GetValueOrDefault(r.ProductId);

            return new ProductSearchItem(r.ProductId, r.ProductUnitId, r.Sku, r.Name, m.Barcode ?? r.FirstBarcode,
                r.UnitCode, r.UnitName, r.Conversion, r.AllowDecimal, r.AllowQtyMultiplier, r.TrackExpiry, r.Brand, r.Category,
                r.ShelfLocation, PriceResolver.Resolve(candidates, node.BranchId, now, 1m), DiscountPrice: null, tiers, baseQty,
                r.Conversion > 0 ? StorageScale.Round(baseQty / r.Conversion) : 0m, m.By, r.BaseUnitCode, r.BaseAllowDecimal);
        }).ToList();
    }

    /// <summary>Treat <c>%</c>, <c>_</c> and <c>\</c> typed by the user literally.</summary>
    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record Match(Guid UnitId, string By, string? Barcode);

    private sealed record UnitRow(
        Guid ProductUnitId, Guid ProductId, string Sku, string Name, string UnitCode, string UnitName, decimal Conversion,
        bool AllowDecimal, bool AllowQtyMultiplier, bool TrackExpiry, string? Brand, string? Category, string? ShelfLocation,
        string BaseUnitCode, bool BaseAllowDecimal, string? FirstBarcode);
}
