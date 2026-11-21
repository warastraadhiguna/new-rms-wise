using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.NodeService.Stock;

public sealed record StockLocationDto(Guid Id, string Code, string Name, string Type, bool IsSellable);

/// <summary>One product at one location ("Form Stok Total"). No purchase price or cost: those are HQ data.</summary>
/// <param name="BaseQty">Stock in the base unit (sum of the location's expiry buckets).</param>
/// <param name="DisplayUnitCode">The product's largest unit, used for the mixed text ("1 BOX 14 PCS").</param>
public sealed record StockTotalDto(
    Guid ProductId,
    string Sku,
    string Name,
    string? Category,
    string? Brand,
    string? ShelfLocation,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    string DisplayUnitCode,
    decimal DisplayConversion,
    decimal BaseQty);

public sealed record StockTotalsPage(int Total, int Offset, int Limit, IReadOnlyList<StockTotalDto> Items);

/// <param name="ExpiryDate">null = stock without an expiry date.</param>
/// <param name="Status">no_expiry | ok | near_expiry | expired (see <see cref="ExpiryStatus"/>).</param>
public sealed record StockExpiryRowDto(Guid LocationId, string LocationCode, DateOnly? ExpiryDate, decimal BaseQty, string Status);

/// <summary>"Form Cek Stok": one product's stock per expiry date.</summary>
/// <param name="Today">The branch's local date the statuses were computed for.</param>
public sealed record StockExpiryDto(
    Guid ProductId,
    string Sku,
    string Name,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    string DisplayUnitCode,
    decimal DisplayConversion,
    bool TrackExpiry,
    DateOnly Today,
    int WarningDays,
    IReadOnlyList<StockExpiryRowDto> Rows);

/// <summary>Read-only stock screens for store/warehouse admins (permission stock.view). Only this node's locations.</summary>
public sealed class StockQueryService(DbConnectionFactory connections, NodeIdentity nodeIdentity, TimeProvider clock)
{
    public const int MaxPageSize = 1000;

    public async Task<IReadOnlyList<StockLocationDto>> GetLocationsAsync(CancellationToken ct = default)
    {
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);
        return (await c.QueryAsync<StockLocationDto>(new CommandDefinition(
            """
            SELECT id AS Id, code AS Code, name AS Name, type AS Type, is_sellable AS IsSellable
            FROM locations WHERE node_id = @nodeId AND deleted_at IS NULL ORDER BY is_sellable DESC, code
            """, new { nodeId = node.Id }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Every product that has stock rows at a location of this node, one row per product and location, sorted by
    /// name. <paramref name="query"/>: exact barcode or SKU, or every word in the name. Paged (max 1000 per page).
    /// </summary>
    public async Task<StockTotalsPage> GetTotalsAsync(string? query, Guid? locationId, int offset, int limit, CancellationToken ct = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, MaxPageSize);
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        var parameters = new DynamicParameters(new { nodeId = node.Id, locationId, offset, limit });
        var filter = SearchFilter((query ?? "").Trim(), parameters);

        const string joins =
            """
            FROM stock_balances sb
            JOIN locations l ON l.id = sb.location_id AND l.node_id = @nodeId AND l.deleted_at IS NULL
            JOIN products p ON p.id = sb.product_id
            """;
        var where = $"WHERE (@locationId IS NULL OR sb.location_id = @locationId) {filter}";

        var total = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM (SELECT 1 {joins} {where} GROUP BY sb.product_id, sb.location_id) x", parameters, cancellationToken: ct));

        var rows = (await c.QueryAsync<TotalRow>(new CommandDefinition(
            $"""
            SELECT p.id AS ProductId, p.sku AS Sku, p.name AS Name, cat.name AS Category, b.name AS Brand,
                   p.shelf_location AS ShelfLocation, l.id AS LocationId, l.code AS LocationCode, l.name AS LocationName,
                   bu.code AS BaseUnitCode, bu.allow_decimal AS BaseAllowDecimal, SUM(sb.base_qty) AS BaseQty
            {joins}
            JOIN units bu ON bu.id = p.base_unit_id
            LEFT JOIN categories cat ON cat.id = p.category_id
            LEFT JOIN brands b ON b.id = p.brand_id
            {where}
            GROUP BY p.id, p.sku, p.name, cat.name, b.name, p.shelf_location, l.id, l.code, l.name, bu.code, bu.allow_decimal
            ORDER BY p.name, p.sku, l.code
            LIMIT @limit OFFSET @offset
            """, parameters, cancellationToken: ct))).ToList();

        var units = await LargestUnitsAsync(c, rows.Select(r => r.ProductId).Distinct().ToList(), ct);
        var items = rows.Select(r =>
        {
            var (unit, conversion) = units.GetValueOrDefault(r.ProductId, (r.BaseUnitCode, 1m));
            return new StockTotalDto(r.ProductId, r.Sku, r.Name, r.Category, r.Brand, r.ShelfLocation, r.LocationId,
                r.LocationCode, r.LocationName, r.BaseUnitCode, r.BaseAllowDecimal, unit, conversion, r.BaseQty);
        }).ToList();

        return new StockTotalsPage(total, offset, limit, items);
    }

    /// <summary>One product's non-zero expiry buckets at this node's locations (or one location), FEFO order.</summary>
    public async Task<StockExpiryDto> GetExpiryAsync(Guid productId, Guid? locationId, CancellationToken ct = default)
    {
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        var product = await c.QuerySingleOrDefaultAsync<ProductRow>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sku AS Sku, p.name AS Name, bu.code AS BaseUnitCode, bu.allow_decimal AS BaseAllowDecimal,
                   p.track_expiry AS TrackExpiry
            FROM products p JOIN units bu ON bu.id = p.base_unit_id WHERE p.id = @productId
            """, new { productId }, cancellationToken: ct))
            ?? throw ApiException.NotFound("unknown_product", $"Product {productId} does not exist.");

        var buckets = await c.QueryAsync<(Guid LocationId, string LocationCode, DateTime ExpiryKey, decimal Qty)>(new CommandDefinition(
            """
            SELECT sb.location_id, l.code, sb.expiry_key, sb.base_qty
            FROM stock_balances sb
            JOIN locations l ON l.id = sb.location_id AND l.node_id = @nodeId AND l.deleted_at IS NULL
            WHERE sb.product_id = @productId AND (@locationId IS NULL OR sb.location_id = @locationId) AND sb.base_qty <> 0
            ORDER BY sb.expiry_key, l.code
            """, new { nodeId = node.Id, productId, locationId }, cancellationToken: ct));

        var warningDays = await WarningDaysAsync(c, node.BranchId, ct);
        var today = BranchClock.Today(clock.GetUtcNow().UtcDateTime, node.TimeZone);
        var (unit, conversion) = (await LargestUnitsAsync(c, [productId], ct)).GetValueOrDefault(productId, (product.BaseUnitCode, 1m));

        var rows = buckets.Select(b =>
        {
            var expiry = StockExpiryKey.FromKey(DateOnly.FromDateTime(b.ExpiryKey));
            return new StockExpiryRowDto(b.LocationId, b.LocationCode, expiry, b.Qty,
                ExpiryStatus.ToCode(ExpiryStatus.Evaluate(expiry, today, warningDays)));
        }).ToList();

        return new StockExpiryDto(product.Id, product.Sku, product.Name, product.BaseUnitCode, product.BaseAllowDecimal,
            unit, conversion, product.TrackExpiry, today, warningDays, rows);
    }

    /// <summary>Exact SKU or barcode, or every word of the name (max 8 words, LIKE wildcards typed literally).</summary>
    private static string SearchFilter(string query, DynamicParameters parameters)
    {
        if (query.Length == 0)
            return "";
        parameters.Add("q", query);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        var nameConditions = words.Select((w, i) =>
        {
            parameters.Add($"w{i}", "%" + Catalog.ProductSearchService.EscapeLike(w) + "%");
            return $"p.name LIKE @w{i} ESCAPE '\\\\'";
        });
        return $"""
            AND (p.sku = @q
                 OR EXISTS (SELECT 1 FROM product_barcodes pb JOIN product_units pu ON pu.id = pb.product_unit_id
                            WHERE pu.product_id = p.id AND pb.barcode = @q AND pb.deleted_at IS NULL)
                 OR ({string.Join(" AND ", nameConditions)}))
            """;
    }

    /// <summary>Per product: the active unit with the largest conversion (ties: lowest sort order).</summary>
    private static async Task<Dictionary<Guid, (string Code, decimal Conversion)>> LargestUnitsAsync(
        MySqlConnection c, IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0)
            return [];
        var units = await c.QueryAsync<(Guid ProductId, string Code, decimal Conversion, short SortOrder)>(new CommandDefinition(
            """
            SELECT pu.product_id, u.code, pu.conversion, pu.sort_order FROM product_units pu JOIN units u ON u.id = pu.unit_id
            WHERE pu.product_id IN @productIds AND pu.deleted_at IS NULL AND pu.conversion > 0
            """, new { productIds }, cancellationToken: ct));
        return units.GroupBy(u => u.ProductId).ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(u => u.Conversion).ThenBy(u => u.SortOrder).Select(u => (u.Code, u.Conversion)).First());
    }

    /// <summary>Setting stock.expiry_warning_days (branch row wins), default 30.</summary>
    private static async Task<int> WarningDaysAsync(MySqlConnection c, Guid branchId, CancellationToken ct)
    {
        var json = await c.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT CAST(setting_value AS CHAR) FROM settings
            WHERE setting_key = @key AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            ORDER BY branch_id IS NULL LIMIT 1
            """, new { key = ExpiryStatus.WarningDaysSetting, branchId }, cancellationToken: ct));
        if (json is null)
            return ExpiryStatus.DefaultWarningDays;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Number when doc.RootElement.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(doc.RootElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => ExpiryStatus.DefaultWarningDays,
        };
    }

    private sealed record TotalRow(
        Guid ProductId, string Sku, string Name, string? Category, string? Brand, string? ShelfLocation, Guid LocationId,
        string LocationCode, string LocationName, string BaseUnitCode, bool BaseAllowDecimal, decimal BaseQty);

    private sealed record ProductRow(Guid Id, string Sku, string Name, string BaseUnitCode, bool BaseAllowDecimal, bool TrackExpiry);
}
