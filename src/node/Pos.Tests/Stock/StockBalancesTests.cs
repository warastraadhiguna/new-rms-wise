using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

public class StockBalancesTests
{
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly Guid Loc1 = Guid.CreateVersion7();
    private static readonly Guid Loc2 = Guid.CreateVersion7();
    private static readonly Guid Prod1 = Guid.CreateVersion7();
    private static readonly Guid Prod2 = Guid.CreateVersion7();

    private static StockLedgerEntry Entry(Guid location, Guid product, DateOnly? expiry, decimal qty) =>
        new(Guid.CreateVersion7(), Node, location, product, expiry, qty,
            StockDocTypes.Adjustment, Guid.CreateVersion7(), null, Utc("2026-11-20T00:00:00Z"));

    [Fact]
    public void Apply_adds_to_existing_and_creates_missing_keys()
    {
        var balances = new Dictionary<StockBalanceKey, decimal>
        {
            [new(Loc1, Prod1, Day("2027-01-01"))] = 5m,
            [new(Loc1, Prod1, null)] = 2m,
        };

        var result = StockBalances.Apply(balances,
        [
            Entry(Loc1, Prod1, Day("2027-01-01"), -3m),
            Entry(Loc1, Prod1, null, -4m),
            Entry(Loc1, Prod1, Day("2027-06-30"), 10m),
        ]);

        Assert.Equal(2m, result[new(Loc1, Prod1, Day("2027-01-01"))]);
        Assert.Equal(-2m, result[new(Loc1, Prod1, null)]);
        Assert.Equal(10m, result[new(Loc1, Prod1, Day("2027-06-30"))]);
    }

    [Fact]
    public void Apply_keeps_zero_balances()
    {
        var key = new StockBalanceKey(Loc1, Prod1, null);
        var result = StockBalances.Apply(new Dictionary<StockBalanceKey, decimal> { [key] = 3m }, [Entry(Loc1, Prod1, null, -3m)]);

        Assert.Equal(0m, result[key]);
    }

    [Fact]
    public void Apply_does_not_mutate_input()
    {
        var key = new StockBalanceKey(Loc1, Prod1, null);
        var balances = new Dictionary<StockBalanceKey, decimal> { [key] = 3m };

        StockBalances.Apply(balances, [Entry(Loc1, Prod1, null, -1m), Entry(Loc2, Prod1, null, 1m)]);

        Assert.Single(balances);
        Assert.Equal(3m, balances[key]);
    }

    [Fact]
    public void Apply_with_no_entries_returns_same_values()
    {
        var key = new StockBalanceKey(Loc1, Prod1, null);
        var result = StockBalances.Apply(new Dictionary<StockBalanceKey, decimal> { [key] = 3m }, []);

        Assert.Equal(3m, Assert.Single(result).Value);
    }

    [Fact]
    public void Apply_total_equals_initial_total_plus_entries()
    {
        var balances = new Dictionary<StockBalanceKey, decimal>
        {
            [new(Loc1, Prod1, Day("2027-01-01"))] = 5m,
            [new(Loc2, Prod2, null)] = -1.5m,
        };
        StockLedgerEntry[] entries =
        [
            Entry(Loc1, Prod1, Day("2027-01-01"), -2m),
            Entry(Loc2, Prod2, null, 0.25m),
            Entry(Loc2, Prod1, Day("2027-02-01"), 7m),
        ];

        var result = StockBalances.Apply(balances, entries);

        Assert.Equal(balances.Values.Sum() + entries.Sum(e => e.BaseQty), result.Values.Sum());
    }

    [Fact]
    public void BucketsFor_filters_and_orders_dated_first_then_null()
    {
        var balances = new Dictionary<StockBalanceKey, decimal>
        {
            [new(Loc1, Prod1, null)] = 1m,
            [new(Loc1, Prod1, Day("2027-03-01"))] = 3m,
            [new(Loc1, Prod1, Day("2027-01-01"))] = 2m,
            [new(Loc2, Prod1, Day("2027-01-01"))] = 99m,
            [new(Loc1, Prod2, null)] = 99m,
        };

        var buckets = StockBalances.BucketsFor(balances, Loc1, Prod1);

        Assert.Equal(
            [new StockBucket(Day("2027-01-01"), 2m), new StockBucket(Day("2027-03-01"), 3m), new StockBucket(null, 1m)],
            buckets);
    }

    [Fact]
    public void Expiry_key_maps_null_to_sentinel_and_back()
    {
        Assert.Equal(new DateOnly(9999, 12, 31), StockExpiryKey.ToKey(null));
        Assert.Equal(Day("2027-01-01"), StockExpiryKey.ToKey(Day("2027-01-01")));
        Assert.Null(StockExpiryKey.FromKey(new DateOnly(9999, 12, 31)));
        Assert.Equal(Day("2027-01-01"), StockExpiryKey.FromKey(Day("2027-01-01")));
    }
}
