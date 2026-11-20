using Pos.Shared.Stock;
using Pos.Shared.Units;

namespace Pos.Tests.Stock;

public class OutboundAllocationTests
{
    private static readonly DateOnly Today = Day("2026-11-20");

    private static StockBucket B(string? expiry, string qty) => new(expiry is null ? null : Day(expiry), D(qty));
    private static StockAllocation A(string? expiry, string qty) => new(expiry is null ? null : Day(expiry), D(qty));

    private static IReadOnlyList<StockAllocation> Out(
        StockBucket[] buckets, string qty, bool trackExpiry = true, bool allowExpired = false) =>
        StockAllocator.AllocateOutbound(buckets, D(qty), trackExpiry, Today, allowExpired);

    [Fact]
    public void Earliest_expiry_is_taken_first()
    {
        var result = Out([B("2027-03-01", "5"), B("2027-01-01", "3"), B(null, "10")], "6");

        Assert.Equal([A("2027-01-01", "3"), A("2027-03-01", "3")], result);
    }

    [Fact]
    public void Null_bucket_is_taken_last()
    {
        var result = Out([B(null, "10"), B("2027-01-01", "2")], "5");

        Assert.Equal([A("2027-01-01", "2"), A(null, "3")], result);
    }

    [Fact]
    public void Shortage_goes_to_null_bucket_even_when_it_does_not_exist()
    {
        var result = Out([B("2027-01-01", "2")], "5");

        Assert.Equal([A("2027-01-01", "2"), A(null, "3")], result);
    }

    [Fact]
    public void Shortage_goes_to_null_bucket_even_when_it_is_already_negative()
    {
        var result = Out([B(null, "-4")], "3");

        Assert.Equal([A(null, "3")], result);
    }

    [Fact]
    public void No_stock_at_all_goes_to_null_bucket()
    {
        Assert.Equal([A(null, "2")], Out([], "2"));
    }

    [Fact]
    public void Exact_consumption_of_dated_bucket_creates_no_null_allocation()
    {
        Assert.Equal([A("2027-01-01", "3")], Out([B("2027-01-01", "3"), B(null, "5")], "3"));
    }

    [Fact]
    public void Expired_bucket_is_skipped()
    {
        var result = Out([B("2026-11-19", "5"), B("2027-01-01", "5")], "3");

        Assert.Equal([A("2027-01-01", "3")], result);
    }

    [Fact]
    public void Bucket_expiring_today_is_not_expired()
    {
        Assert.Equal([A("2026-11-20", "3")], Out([B("2026-11-20", "5")], "3"));
    }

    [Fact]
    public void Expired_stock_only_goes_to_null_bucket()
    {
        Assert.Equal([A(null, "2")], Out([B("2026-11-01", "5")], "2"));
    }

    [Fact]
    public void Allow_expired_takes_expired_bucket_first()
    {
        var result = Out([B("2026-11-19", "5"), B("2027-01-01", "5")], "7", allowExpired: true);

        Assert.Equal([A("2026-11-19", "5"), A("2027-01-01", "2")], result);
    }

    [Fact]
    public void Untracked_product_uses_remaining_dated_buckets_first()
    {
        var result = Out([B("2027-01-01", "5"), B(null, "3")], "4", trackExpiry: false);

        Assert.Equal([A("2027-01-01", "4")], result);
    }

    [Fact]
    public void Untracked_product_also_uses_expired_dated_buckets_fefo_then_null()
    {
        var result = Out([B("2027-01-01", "1"), B(null, "3"), B("2026-11-01", "2")], "5", trackExpiry: false);

        Assert.Equal([A("2026-11-01", "2"), A("2027-01-01", "1"), A(null, "2")], result);
    }

    [Fact]
    public void Untracked_product_without_dated_stock_goes_to_null_bucket()
    {
        Assert.Equal([A(null, "4")], Out([B(null, "1")], "4", trackExpiry: false));
    }

    [Fact]
    public void Product_switched_to_untracked_sells_its_dated_stock_first()
    {
        var node = Guid.CreateVersion7();
        var location = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var at = Utc("2026-11-20T03:00:00Z");
        IReadOnlyDictionary<StockBalanceKey, decimal> balances = new Dictionary<StockBalanceKey, decimal>();

        // while tracked: receive 10 expiring 2027-01-01 and 4 expiring 2026-11-10 (expired by now)
        foreach (var (expiry, qty) in new[] { (Day("2027-01-01"), 10m), (Day("2026-11-10"), 4m) })
        {
            var receipt = StockAllocator.AllocateInbound(StockBalances.BucketsFor(balances, location, product), qty, expiry, trackExpiry: true);
            var line = new StockDocumentLine(node, location, product, StockDocTypes.GoodsReceipt, Guid.CreateVersion7(), null, at);
            balances = StockBalances.Apply(balances, StockLedgerBuilder.Build(line, StockDirection.In, receipt));
        }

        // flag switched off: sell 16
        var sale = StockAllocator.AllocateOutbound(StockBalances.BucketsFor(balances, location, product), 16m, trackExpiry: false, Today);
        var saleLine = new StockDocumentLine(node, location, product, StockDocTypes.Sale, Guid.CreateVersion7(), null, at);
        balances = StockBalances.Apply(balances, StockLedgerBuilder.Build(saleLine, StockDirection.Out, sale));

        Assert.Equal([A("2026-11-10", "4"), A("2027-01-01", "10"), A(null, "2")], sale);
        Assert.Equal(0m, balances[new(location, product, Day("2026-11-10"))]);
        Assert.Equal(0m, balances[new(location, product, Day("2027-01-01"))]);
        Assert.Equal(-2m, balances[new(location, product, null)]);
    }

    [Fact]
    public void Untracked_product_ignores_allow_expired_flag()
    {
        StockBucket[] buckets = [B("2026-11-01", "2"), B("2027-01-01", "2")];

        Assert.Equal(Out(buckets, "3", trackExpiry: false, allowExpired: true), Out(buckets, "3", trackExpiry: false));
    }

    [Fact]
    public void Zero_qty_allocates_nothing()
    {
        Assert.Empty(Out([B("2027-01-01", "5")], "0"));
    }

    [Fact]
    public void Rejects_negative_qty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Out([B("2027-01-01", "5")], "-1"));
    }

    [Fact]
    public void Rejects_qty_with_more_than_four_decimals()
    {
        Assert.Throws<InvalidQuantityException>(() => Out([B("2027-01-01", "5")], "0.00001"));
    }

    [Fact]
    public void Zero_and_negative_dated_buckets_are_skipped()
    {
        var result = Out([B("2027-01-01", "0"), B("2027-02-01", "-1"), B("2027-03-01", "4")], "2");

        Assert.Equal([A("2027-03-01", "2")], result);
    }

    [Fact]
    public void Decimal_quantities()
    {
        var result = Out([B("2027-01-01", "0.25"), B(null, "1")], "0.5");

        Assert.Equal([A("2027-01-01", "0.25"), A(null, "0.25")], result);
    }

    [Fact]
    public void Duplicate_dates_are_merged_into_one_bucket()
    {
        var result = Out([B("2027-01-01", "2"), B("2027-02-01", "5"), B("2027-01-01", "3")], "6");

        Assert.Equal([A("2027-01-01", "5"), A("2027-02-01", "1")], result);
    }

    [Fact]
    public void Result_does_not_depend_on_input_order()
    {
        StockBucket[] buckets =
        [
            B("2027-02-01", "2"), B(null, "4"), B("2027-01-01", "1"),
            B("2027-01-01", "2"), B("2026-12-01", "1"), B("2026-11-01", "9"),
        ];
        var expected = Out(buckets, "8");

        var random = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var shuffled = buckets.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, Out(shuffled, "8"));
        }
    }
}
