namespace Pos.Shared.Sales;

/// <summary>One <c>prices</c> row of a product unit. <c>BranchId</c> null = default price for all branches.</summary>
public readonly record struct PriceCandidate(Guid? BranchId, decimal Price, decimal MinQty, DateTime EffectiveFrom);

/// <summary>
/// Picks the selling price of a product unit:
/// 1. only rows with <c>effective_from &lt;= now</c>; rows of other branches are ignored;
/// 2. per <c>min_qty</c> tier, the row with the latest <c>effective_from</c> (ties: the lower price);
/// 3. the best tier is the largest <c>min_qty &lt;= qty</c>, looked up in the branch rows AND the default rows;
/// 4. the tier with the larger <c>min_qty</c> wins; on equal <c>min_qty</c> the branch price wins;
/// 5. below every tier (0,5 kg priced from 1 kg) the smallest <c>min_qty</c> applies (branch wins a tie).
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
        var branchRows = effective.Where(c => c.BranchId == branchId).ToList();
        var defaultRows = effective.Where(c => c.BranchId is null).ToList();
        var branch = BestTier(branchRows, qty);
        var fallback = BestTier(defaultRows, qty);

        return (branch, fallback) switch
        {
            (null, null) => SmallestTier(branchRows, defaultRows)?.Price,
            ({ } b, null) => b.Price,
            (null, { } d) => d.Price,
            ({ } b, { } d) => b.MinQty >= d.MinQty ? b.Price : d.Price,
        };
    }

    /// <summary>
    /// Below every tier (e.g. 0,5 kg with a price from 1 kg) the tier with the smallest <c>min_qty</c> applies;
    /// on equal <c>min_qty</c> the branch price wins.
    /// </summary>
    private static PriceCandidate? SmallestTier(IReadOnlyList<PriceCandidate> branchRows, IReadOnlyList<PriceCandidate> defaultRows)
    {
        var branch = Smallest(branchRows);
        var fallback = Smallest(defaultRows);
        return (branch, fallback) switch
        {
            ({ } b, { } d) => b.MinQty <= d.MinQty ? b : d,
            _ => branch ?? fallback,
        };

        static PriceCandidate? Smallest(IReadOnlyList<PriceCandidate> rows) =>
            rows.Count == 0
                ? null
                : rows.Where(r => r.MinQty == rows.Min(x => x.MinQty))
                    .OrderByDescending(r => r.EffectiveFrom).ThenBy(r => r.Price).First();
    }

    private static PriceCandidate? BestTier(IEnumerable<PriceCandidate> rows, decimal qty)
    {
        var tier = rows
            .Where(r => r.MinQty <= qty)
            .GroupBy(r => r.MinQty)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault();

        return tier?
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenBy(r => r.Price)
            .First();
    }
}
