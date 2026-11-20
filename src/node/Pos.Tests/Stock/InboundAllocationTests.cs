using Pos.Shared.Stock;
using Pos.Shared.Units;

namespace Pos.Tests.Stock;

public class InboundAllocationTests
{
    private static readonly DateOnly E = Day("2027-06-30");

    private static StockBucket B(string? expiry, string qty) => new(expiry is null ? null : Day(expiry), D(qty));
    private static StockAllocation A(DateOnly? expiry, string qty) => new(expiry, D(qty));

    private static IReadOnlyList<StockAllocation> In(
        StockBucket[] buckets, string qty, DateOnly? expiry, bool trackExpiry = true) =>
        StockAllocator.AllocateInbound(buckets, D(qty), expiry, trackExpiry);

    [Fact]
    public void Goes_to_expiry_bucket_when_null_bucket_is_not_negative()
    {
        Assert.Equal([A(E, "5")], In([B(null, "2")], "5", E));
    }

    [Fact]
    public void Goes_to_expiry_bucket_when_there_is_no_null_bucket()
    {
        Assert.Equal([A(E, "5")], In([B("2027-01-01", "3")], "5", E));
    }

    [Fact]
    public void Negative_null_bucket_is_covered_first()
    {
        Assert.Equal([A(null, "3"), A(E, "2")], In([B(null, "-3")], "5", E));
    }

    [Fact]
    public void Negative_null_bucket_larger_than_receipt_takes_everything()
    {
        Assert.Equal([A(null, "4")], In([B(null, "-10")], "4", E));
    }

    [Fact]
    public void Negative_null_bucket_exactly_covered()
    {
        Assert.Equal([A(null, "5")], In([B(null, "-5")], "5", E));
    }

    [Fact]
    public void Decimal_cover()
    {
        Assert.Equal([A(null, "0.25"), A(E, "0.75")], In([B(null, "-0.25")], "1", E));
    }

    [Fact]
    public void Negative_dated_bucket_is_not_covered()
    {
        Assert.Equal([A(E, "5")], In([B("2027-01-01", "-2")], "5", E));
    }

    [Fact]
    public void Receipt_without_expiry_goes_to_null_bucket()
    {
        Assert.Equal([A(null, "5")], In([B(null, "-3")], "5", expiry: null));
    }

    [Fact]
    public void Untracked_product_ignores_expiry()
    {
        Assert.Equal([A(null, "5")], In([B(null, "2")], "5", E, trackExpiry: false));
    }

    [Fact]
    public void Zero_qty_allocates_nothing()
    {
        Assert.Empty(In([B(null, "-3")], "0", E));
    }

    [Fact]
    public void Rejects_negative_qty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => In([], "-1", E));
    }

    [Fact]
    public void Rejects_qty_with_more_than_four_decimals()
    {
        Assert.Throws<InvalidQuantityException>(() => In([], "1.00001", E));
    }

    [Fact]
    public void Rejects_expiry_equal_to_no_expiry_sentinel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => In([], "1", StockExpiryKey.NoExpiry));
    }
}
