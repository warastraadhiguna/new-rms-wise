namespace Pos.Shared.Sales;

/// <summary>Effective price from <c>min_qty</c> upward (as resolved by the server for this branch).</summary>
public sealed record PriceTier(decimal MinQty, decimal Price);

/// <summary>One sellable product unit as the cashier screen knows it.</summary>
/// <param name="StockBaseQty">Stock in the sellable locations when the item was looked up (base units).</param>
public sealed record CartItem(
    Guid ProductId,
    Guid ProductUnitId,
    string Code,
    string Name,
    string UnitCode,
    decimal Conversion,
    bool AllowDecimal,
    bool AllowQtyMultiplier,
    IReadOnlyList<PriceTier> PriceTiers,
    decimal StockBaseQty);

public sealed record CartLine(CartItem Item, decimal Qty)
{
    /// <summary>Unit price for this quantity (wholesale tiers), or 0 when there is none.</summary>
    public decimal Price => Cart.PriceFor(Item.PriceTiers, Qty) ?? 0m;

    public decimal SubTotal => SaleLineCalculator.Calculate(Qty, Price).LineTotal;

    public decimal BaseQty => Qty * Item.Conversion;
}

/// <summary>More of a product in the cart than the sellable stock (a warning: negative stock is allowed).</summary>
public sealed record StockWarning(Guid ProductId, string Name, decimal AvailableBaseQty, decimal InCartBaseQty);

/// <summary>A cart rule was broken. <c>Code</c> is stable; the client shows its own text.</summary>
public sealed class CartException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The cashier's cart before payment. Prices shown here mirror the server (tiers per branch); the server
/// recalculates everything when the sale is saved.
/// Rules: qty &gt; 0; whole numbers unless the unit allows decimals; products without the qty multiplier
/// (RMS 2 isCanPengali = 0) stay at qty 1 per line and are never merged; the same unit added again is merged.
/// </summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;

    public bool IsEmpty => _lines.Count == 0;

    public decimal Total => _lines.Sum(l => l.SubTotal);

    /// <returns>Index of the new or increased line.</returns>
    public int Add(CartItem item, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(item);
        Validate(item, qty);

        var existing = item.AllowQtyMultiplier ? _lines.FindIndex(l => l.Item.ProductUnitId == item.ProductUnitId) : -1;
        if (existing >= 0)
        {
            var merged = _lines[existing].Qty + qty;
            Validate(item, merged);
            _lines[existing] = new CartLine(item, merged);   // the latest lookup (price, stock) wins
            return existing;
        }

        _lines.Add(new CartLine(item, qty));
        return _lines.Count - 1;
    }

    public void SetQty(int index, decimal qty)
    {
        var line = _lines[index];
        Validate(line.Item, qty);
        _lines[index] = line with { Qty = qty };
    }

    /// <summary>Switches a line to another unit of the same product; merges into a line that already has that unit.</summary>
    /// <returns>Index of the resulting line.</returns>
    public int ChangeUnit(int index, CartItem unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var line = _lines[index];
        if (unit.ProductId != line.Item.ProductId)
            throw new CartException("different_product", "A line can only switch to another unit of the same product.");

        var other = unit.AllowQtyMultiplier
            ? _lines.FindIndex(l => l.Item.ProductUnitId == unit.ProductUnitId && !ReferenceEquals(l, line))
            : -1;
        if (other >= 0)
        {
            var merged = _lines[other].Qty + line.Qty;
            Validate(unit, merged);
            _lines[other] = new CartLine(unit, merged);
            _lines.RemoveAt(index);
            return other > index ? other - 1 : other;
        }

        Validate(unit, line.Qty);
        _lines[index] = new CartLine(unit, line.Qty);
        return index;
    }

    public void RemoveAt(int index) => _lines.RemoveAt(index);

    public void Clear() => _lines.Clear();

    public void Restore(IEnumerable<CartLine> lines)
    {
        _lines.Clear();
        _lines.AddRange(lines);
    }

    public IReadOnlyList<StockWarning> StockWarnings() =>
        _lines.GroupBy(l => l.Item.ProductId)
            .Select(g => new
            {
                Last = g.Last().Item,
                InCart = g.Sum(l => l.BaseQty),
            })
            .Where(x => x.InCart > x.Last.StockBaseQty)
            .Select(x => new StockWarning(x.Last.ProductId, x.Last.Name, x.Last.StockBaseQty, x.InCart))
            .ToList();

    /// <summary>
    /// The tier with the largest <c>MinQty &lt;= qty</c>, like the server; below every tier the smallest tier
    /// (0,5 kg priced from 1 kg). Null only when there is no price at all.
    /// </summary>
    public static decimal? PriceFor(IReadOnlyList<PriceTier> tiers, decimal qty) =>
        tiers.Where(t => t.MinQty <= qty).OrderByDescending(t => t.MinQty).Select(t => (decimal?)t.Price).FirstOrDefault()
        ?? tiers.OrderBy(t => t.MinQty).Select(t => (decimal?)t.Price).FirstOrDefault();

    private static void Validate(CartItem item, decimal qty)
    {
        if (qty <= 0)
            throw new CartException("invalid_quantity", "Quantity must be greater than zero.");
        if (!item.AllowDecimal && decimal.Truncate(qty) != qty)
            throw new CartException("decimal_not_allowed", $"Unit {item.UnitCode} needs a whole quantity.");
        if (!item.AllowQtyMultiplier && qty > 1)
            throw new CartException("qty_multiplier_not_allowed", $"{item.Name} must be scanned one by one.");
        if (PriceFor(item.PriceTiers, qty) is null)
            throw new CartException("no_price", $"{item.Name} has no price for quantity {qty}.");
    }
}
