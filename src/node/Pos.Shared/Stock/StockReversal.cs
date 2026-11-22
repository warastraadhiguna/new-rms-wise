namespace Pos.Shared.Stock;

/// <summary>An inbound movement that puts sold stock back into a known bucket.</summary>
public sealed record StockReturnMovement(Guid LocationId, Guid ProductId, DateOnly? ExpiryDate, decimal BaseQty, Guid? DocLineId);

/// <summary>Stock coming back from a sale: a void puts everything back; a return puts part of a line back.</summary>
public static class StockReversal
{
    /// <summary>
    /// The outbound ledger rows of a document (e.g. a sale) as inbound movements into the same location, product and
    /// expiry bucket, one per bucket and document line (rows of one bucket are summed). Inbound rows are ignored.
    /// </summary>
    public static IReadOnlyList<StockReturnMovement> ReverseOutbound(IEnumerable<StockLedgerEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(r => r.BaseQty < 0)
            .GroupBy(r => (r.LocationId, r.ProductId, r.ExpiryDate, r.DocLineId))
            .Select(g => new StockReturnMovement(g.Key.LocationId, g.Key.ProductId, g.Key.ExpiryDate, -g.Sum(r => r.BaseQty), g.Key.DocLineId))
            .ToList();
    }

    /// <summary>
    /// Expiry dates for a returned quantity of one sale line: the buckets the sale took, earliest first, minus what
    /// earlier returns of that line already took back (so stock that expires first is sold first again).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Zero/negative, or more than sold minus already returned.</exception>
    public static IReadOnlyList<StockAllocation> AllocateReturn(
        IReadOnlyList<StockAllocation> sold, IReadOnlyList<StockAllocation> alreadyReturned, decimal returnBaseQty)
    {
        ArgumentNullException.ThrowIfNull(sold);
        ArgumentNullException.ThrowIfNull(alreadyReturned);
        if (returnBaseQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(returnBaseQty), returnBaseQty, "The returned quantity must be positive.");

        var remaining = sold
            .GroupBy(a => a.ExpiryDate)
            .Select(g => (Expiry: g.Key, Qty: g.Sum(a => a.BaseQty) - alreadyReturned.Where(r => r.ExpiryDate == g.Key).Sum(r => r.BaseQty)))
            .Where(b => b.Qty > 0)
            .OrderBy(b => b.Expiry is null ? 1 : 0).ThenBy(b => b.Expiry)
            .ToList();
        if (returnBaseQty > remaining.Sum(b => b.Qty))
            throw new ArgumentOutOfRangeException(nameof(returnBaseQty), returnBaseQty, "More than was sold and not yet returned.");

        var result = new List<StockAllocation>();
        var left = returnBaseQty;
        foreach (var (expiry, qty) in remaining)
        {
            if (left == 0)
                break;
            var take = Math.Min(qty, left);
            result.Add(new StockAllocation(expiry, take));
            left -= take;
        }
        return result;
    }
}
