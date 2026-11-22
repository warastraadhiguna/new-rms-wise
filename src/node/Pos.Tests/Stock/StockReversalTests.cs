using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

public class StockReversalTests
{
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly Guid Display = Guid.CreateVersion7();
    private static readonly Guid Tea = Guid.CreateVersion7();
    private static readonly Guid Water = Guid.CreateVersion7();
    private static readonly Guid Line1 = Guid.CreateVersion7();
    private static readonly Guid Line2 = Guid.CreateVersion7();
    private static readonly DateOnly E1 = new(2027, 1, 1);
    private static readonly DateOnly E2 = new(2027, 6, 1);

    private static StockLedgerEntry Out(Guid product, DateOnly? expiry, decimal qty, Guid line) =>
        new(Guid.CreateVersion7(), Node, Display, product, expiry, -qty, StockDocTypes.Sale, Guid.CreateVersion7(), line, DateTime.UtcNow);

    [Fact]
    public void Void_puts_every_sold_quantity_back_in_the_same_bucket()
    {
        var sold = new[] { Out(Tea, E1, 3m, Line1), Out(Tea, E2, 2m, Line1), Out(Water, null, 24m, Line2) };

        var back = StockReversal.ReverseOutbound(sold);

        Assert.Equal(
            [(Display, Tea, (DateOnly?)E1, 3m, (Guid?)Line1), (Display, Tea, E2, 2m, Line1), (Display, Water, null, 24m, Line2)],
            back.Select(m => (m.LocationId, m.ProductId, m.ExpiryDate, m.BaseQty, m.DocLineId)));
    }

    [Fact]
    public void Rows_of_one_bucket_and_line_are_summed_and_inbound_rows_are_ignored()
    {
        var inbound = Out(Tea, E1, 1m, Line1) with { BaseQty = 1m };
        var sold = new[] { Out(Tea, E1, 3m, Line1), Out(Tea, E1, 2m, Line1), inbound };

        var back = Assert.Single(StockReversal.ReverseOutbound(sold));

        Assert.Equal(5m, back.BaseQty);
    }

    [Fact]
    public void Nothing_sold_gives_nothing_back()
    {
        Assert.Empty(StockReversal.ReverseOutbound([]));
    }

    [Fact]
    public void A_return_goes_back_to_the_sold_expiry_dates_earliest_first()
    {
        var back = StockReversal.AllocateReturn(
            sold: [new StockAllocation(E2, 2m), new StockAllocation(E1, 3m)], alreadyReturned: [], returnBaseQty: 4m);

        Assert.Equal([new StockAllocation(E1, 3m), new StockAllocation(E2, 1m)], back);
    }

    [Fact]
    public void Earlier_returns_are_taken_off_first()
    {
        var back = StockReversal.AllocateReturn(
            sold: [new StockAllocation(E1, 3m), new StockAllocation(E2, 2m)],
            alreadyReturned: [new StockAllocation(E1, 2m)],
            returnBaseQty: 2m);

        Assert.Equal([new StockAllocation(E1, 1m), new StockAllocation(E2, 1m)], back);
    }

    [Fact]
    public void Without_expiry_and_with_decimals()
    {
        var back = StockReversal.AllocateReturn([new StockAllocation(null, 1.5m)], [new StockAllocation(null, 0.25m)], 1.25m);

        Assert.Equal([new StockAllocation(null, 1.25m)], back);
    }

    [Fact]
    public void Returning_more_than_remains_or_nothing_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StockReversal.AllocateReturn([new StockAllocation(E1, 3m)], [new StockAllocation(E1, 2m)], 1.0001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockReversal.AllocateReturn([new StockAllocation(E1, 3m)], [], 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockReversal.AllocateReturn([new StockAllocation(E1, 3m)], [], -1m));
    }

    [Fact]
    public void Void_and_return_labels_on_the_kartu_stok()
    {
        Assert.Equal("Void Penjualan", StockDocTypes.Label(StockDocTypes.SaleVoid, fromStockCount: false));
        Assert.Equal("Retur", StockDocTypes.Label(StockDocTypes.SaleReturn, fromStockCount: false));
        Assert.True(StockDocTypes.IsDefined(StockDocTypes.SaleVoid));
    }
}
