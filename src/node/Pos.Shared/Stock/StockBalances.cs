namespace Pos.Shared.Stock;

/// <summary>Pure balance arithmetic; the Node Service mirrors this in <c>stock_balances</c> in the same transaction.</summary>
public static class StockBalances
{
    /// <summary>Returns a new balance map with every entry added. The input is not modified; zero balances are kept.</summary>
    public static IReadOnlyDictionary<StockBalanceKey, decimal> Apply(
        IReadOnlyDictionary<StockBalanceKey, decimal> balances,
        IEnumerable<StockLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(entries);

        var result = new Dictionary<StockBalanceKey, decimal>(balances);
        foreach (var entry in entries)
        {
            var key = new StockBalanceKey(entry.LocationId, entry.ProductId, entry.ExpiryDate);
            result[key] = result.GetValueOrDefault(key) + entry.BaseQty;
        }
        return result;
    }

    /// <summary>Buckets of one product at one location: dated ascending, then the null bucket.</summary>
    public static IReadOnlyList<StockBucket> BucketsFor(
        IReadOnlyDictionary<StockBalanceKey, decimal> balances,
        Guid locationId,
        Guid productId)
    {
        ArgumentNullException.ThrowIfNull(balances);

        return balances
            .Where(b => b.Key.LocationId == locationId && b.Key.ProductId == productId)
            .OrderBy(b => StockExpiryKey.ToKey(b.Key.ExpiryDate))
            .Select(b => new StockBucket(b.Key.ExpiryDate, b.Value))
            .ToList();
    }
}
