using Pos.Shared.Units;

namespace Pos.Shared.Stock;

/// <summary>
/// Splits a movement of one product at one location into expiry buckets. Quantities are in base units.
/// Only the null (no/unknown expiry) bucket may go negative.
/// </summary>
public static class StockAllocator
{
    /// <summary>
    /// FEFO for goods going out (sale, delivery, transfer out).
    /// Dated buckets with positive stock are taken earliest expiry first; buckets with expiry before
    /// <paramref name="today"/> (branch date) are skipped unless <paramref name="allowExpired"/>.
    /// Whatever is left goes to the null bucket, which may become negative.
    /// Buckets with the same date are merged, so the result does not depend on input order.
    /// Untracked products (track_expiry switched off later) first drain any remaining dated buckets
    /// FEFO, expired ones included, then the null bucket.
    /// </summary>
    public static IReadOnlyList<StockAllocation> AllocateOutbound(
        IEnumerable<StockBucket> buckets,
        decimal baseQty,
        bool trackExpiry,
        DateOnly today,
        bool allowExpired = false)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        EnsureValidQty(baseQty);

        if (baseQty == 0)
            return [];
        if (!trackExpiry)
            allowExpired = true;

        var candidates = buckets
            .Where(b => b.ExpiryDate is not null)
            .GroupBy(b => b.ExpiryDate!.Value)
            .Select(g => (Expiry: g.Key, Available: g.Sum(b => b.BaseQty)))
            .Where(b => b.Available > 0 && (allowExpired || b.Expiry >= today))
            .OrderBy(b => b.Expiry);

        var result = new List<StockAllocation>();
        var remaining = baseQty;

        foreach (var (expiry, available) in candidates)
        {
            var take = Math.Min(available, remaining);
            result.Add(new StockAllocation(expiry, take));
            remaining -= take;
            if (remaining == 0)
                return result;
        }

        result.Add(new StockAllocation(null, remaining));
        return result;
    }

    /// <summary>
    /// Goods coming in (receipt, return, transfer in) with expiry <paramref name="expiryDate"/>.
    /// A negative null bucket is covered first (goods sold while short are assumed to come from this
    /// delivery); the rest goes to the expiry bucket. Untracked products and receipts without an expiry
    /// go entirely to the null bucket.
    /// </summary>
    public static IReadOnlyList<StockAllocation> AllocateInbound(
        IEnumerable<StockBucket> buckets,
        decimal baseQty,
        DateOnly? expiryDate,
        bool trackExpiry)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        EnsureValidQty(baseQty);
        if (expiryDate == StockExpiryKey.NoExpiry)
            throw new ArgumentOutOfRangeException(nameof(expiryDate), expiryDate,
                "Expiry date is reserved for 'no expiry'; pass null instead.");

        if (baseQty == 0)
            return [];
        if (!trackExpiry || expiryDate is null)
            return [new StockAllocation(null, baseQty)];

        var nullBalance = buckets.Where(b => b.ExpiryDate is null).Sum(b => b.BaseQty);
        var cover = nullBalance < 0 ? Math.Min(baseQty, -nullBalance) : 0m;
        var rest = baseQty - cover;

        var result = new List<StockAllocation>(2);
        if (cover > 0)
            result.Add(new StockAllocation(null, cover));
        if (rest > 0)
            result.Add(new StockAllocation(expiryDate, rest));
        return result;
    }

    private static void EnsureValidQty(decimal baseQty)
    {
        if (baseQty < 0)
            throw new ArgumentOutOfRangeException(nameof(baseQty), baseQty, "Quantity must not be negative.");
        if (!StorageScale.Fits(baseQty))
            throw new InvalidQuantityException(
                $"Quantity {baseQty} has more than {StorageScale.Decimals} decimals.", nameof(baseQty));
    }
}
