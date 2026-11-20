namespace Pos.Shared.Sales;

/// <summary>One <c>prices</c> row of a product unit. <c>BranchId</c> null = default price for all branches.</summary>
public readonly record struct PriceCandidate(Guid? BranchId, decimal Price, decimal MinQty, DateTime EffectiveFrom);

/// <summary>
/// Picks the selling price of a product unit:
/// 1. only rows with <c>effective_from &lt;= now</c>; rows of other branches are ignored;
/// 2. per <c>min_qty</c> tier, the row with the latest <c>effective_from</c> (ties: the lower price);
/// 3. the tier with the largest <c>min_qty &lt;= qty</c>;
/// 4. the branch's own rows are used when they give a price for this qty, otherwise the default rows.
/// </summary>
public static class PriceResolver
{
    public static decimal? Resolve(IEnumerable<PriceCandidate> candidates, Guid branchId, DateTime now, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (now.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(now));
        if (qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), qty, "Quantity must be greater than zero.");

        var effective = candidates.Where(c => c.EffectiveFrom <= now).ToList();

        return FromSet(effective.Where(c => c.BranchId == branchId), qty)
            ?? FromSet(effective.Where(c => c.BranchId is null), qty);
    }

    private static decimal? FromSet(IEnumerable<PriceCandidate> rows, decimal qty)
    {
        var tier = rows
            .Where(r => r.MinQty <= qty)
            .GroupBy(r => r.MinQty)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault();

        return tier?
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenBy(r => r.Price)
            .First()
            .Price;
    }
}
