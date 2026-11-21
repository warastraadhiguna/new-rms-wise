using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

public class StockTransferTests
{
    private static readonly DateOnly Today = Day("2026-11-20");
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly Guid From = Guid.CreateVersion7();
    private static readonly Guid To = Guid.CreateVersion7();
    private static readonly Guid Product = Guid.CreateVersion7();
    private static readonly Guid Doc = Guid.CreateVersion7();
    private static readonly Guid DocLine = Guid.CreateVersion7();
    private static readonly DateTime At = Utc("2026-11-20T03:00:00Z");

    private static StockBucket B(string? expiry, string qty) => new(expiry is null ? null : Day(expiry), D(qty));
    private static StockAllocation A(string? expiry, string qty) => new(expiry is null ? null : Day(expiry), D(qty));

    private static StockTransferRequest Request(string qty, Guid? to = null) =>
        new(Node, Product, From, to ?? To, D(qty), Doc, DocLine, At);

    private static Dictionary<StockBalanceKey, decimal> Balances(
        (Guid Location, string? Expiry, string Qty)[] rows) =>
        rows.ToDictionary(r => new StockBalanceKey(r.Location, Product, r.Expiry is null ? null : Day(r.Expiry)), r => D(r.Qty));

    private static (DateOnly?, decimal)[] Shape(IEnumerable<StockLedgerEntry> entries) =>
        entries.Select(e => (e.ExpiryDate, e.BaseQty)).ToArray();

    // ---- within one node ------------------------------------------------------------------

    [Fact]
    public void Transfer_moves_fefo_allocation_with_same_expiry()
    {
        var balances = Balances([(From, "2027-03-01", "5"), (From, "2027-01-01", "3")]);

        var result = StockTransfer.Transfer(Request("5"), balances, Today, trackExpiry: true);

        Assert.Equal([(Day("2027-01-01"), -3m), (Day("2027-03-01"), -2m)], Shape(result.Out));
        Assert.Equal([(Day("2027-01-01"), 3m), (Day("2027-03-01"), 2m)], Shape(result.In));
    }

    [Fact]
    public void Transfer_covers_negative_null_bucket_at_destination_first()
    {
        var balances = Balances([(From, "2027-01-01", "3"), (From, "2027-03-01", "5"), (To, null, "-2")]);

        var result = StockTransfer.Transfer(Request("5"), balances, Today, trackExpiry: true);

        Assert.Equal([(Day("2027-01-01"), -3m), (Day("2027-03-01"), -2m)], Shape(result.Out));
        Assert.Equal([((DateOnly?)null, 2m), (Day("2027-01-01"), 1m), (Day("2027-03-01"), 2m)], Shape(result.In));

        var after = StockBalances.Apply(balances, result.All);
        Assert.Equal(0m, after[new(To, Product, null)]);
    }

    [Fact]
    public void Transfer_with_shortage_moves_null_bucket_part_as_null()
    {
        var balances = Balances([(From, "2027-01-01", "2")]);

        var result = StockTransfer.Transfer(Request("5"), balances, Today, trackExpiry: true);

        Assert.Equal([(Day("2027-01-01"), -2m), ((DateOnly?)null, -3m)], Shape(result.Out));
        Assert.Equal([(Day("2027-01-01"), 2m), ((DateOnly?)null, 3m)], Shape(result.In));

        var after = StockBalances.Apply(balances, result.All);
        Assert.Equal(-3m, after[new(From, Product, null)]);
    }

    [Fact]
    public void Transfer_skips_expired_unless_allowed()
    {
        var balances = Balances([(From, "2026-11-01", "4"), (From, "2027-01-01", "4")]);

        var normal = StockTransfer.Transfer(Request("3"), balances, Today, trackExpiry: true);
        var expiredAllowed = StockTransfer.Transfer(Request("3"), balances, Today, trackExpiry: true, allowExpired: true);

        Assert.Equal([(Day("2027-01-01"), 3m)], Shape(normal.In));
        Assert.Equal([(Day("2026-11-01"), 3m)], Shape(expiredAllowed.In));
    }

    [Fact]
    public void Transfer_of_untracked_product_drains_dated_stock_at_source_and_lands_in_null_bucket()
    {
        var balances = Balances([(From, "2027-01-01", "2"), (From, null, "5")]);

        var result = StockTransfer.Transfer(Request("4"), balances, Today, trackExpiry: false);

        Assert.Equal([(Day("2027-01-01"), -2m), ((DateOnly?)null, -2m)], Shape(result.Out));
        Assert.Equal([((DateOnly?)null, 2m), ((DateOnly?)null, 2m)], Shape(result.In));
    }

    [Fact]
    public void Transfer_entries_carry_locations_and_document()
    {
        var balances = Balances([(From, "2027-01-01", "5")]);

        var result = StockTransfer.Transfer(Request("2"), balances, Today, trackExpiry: true);

        Assert.All(result.Out, e => Assert.Equal(From, e.LocationId));
        Assert.All(result.In, e => Assert.Equal(To, e.LocationId));
        Assert.All(result.All, e =>
        {
            Assert.Equal(Node, e.NodeId);
            Assert.Equal(Product, e.ProductId);
            Assert.Equal(StockDocTypes.LocationTransfer, e.DocType);
            Assert.Equal(Doc, e.DocId);
            Assert.Equal(DocLine, e.DocLineId);
            Assert.Equal(At, e.OccurredAt);
        });
        Assert.Equal(result.All.Count, result.All.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Transfer_of_zero_creates_no_entries()
    {
        var result = StockTransfer.Transfer(Request("0"), Balances([]), Today, trackExpiry: true);
        Assert.Empty(result.All);
    }

    [Fact]
    public void Transfer_rejects_same_source_and_destination()
    {
        Assert.Throws<ArgumentException>(
            () => StockTransfer.Transfer(Request("1", to: From), Balances([]), Today, trackExpiry: true));
    }

    [Fact]
    public void Transfer_rejects_negative_qty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StockTransfer.Transfer(Request("-1"), Balances([]), Today, trackExpiry: true));
    }

    // ---- across nodes: delivery (sender) and goods receipt (receiver) are separate ----------

    [Fact]
    public void Receipt_reads_expiries_from_delivery_lines()
    {
        // sender: FEFO allocation becomes one delivery line per expiry
        var shipped = StockAllocator.AllocateOutbound(
            [B("2027-01-01", "3"), B("2027-03-01", "5")], 5m, trackExpiry: true, Today);

        // receiver: destination null bucket is -4
        var received = StockTransfer.AllocateReceipt([B(null, "-4")], shipped, trackExpiry: true);

        Assert.Equal(2, received.Count);
        Assert.Equal(A("2027-01-01", "3"), received[0].Shipped);
        Assert.Equal([A(null, "3")], received[0].Allocations);
        Assert.Equal(A("2027-03-01", "2"), received[1].Shipped);
        Assert.Equal([A(null, "1"), A("2027-03-01", "1")], received[1].Allocations);
        Assert.Equal(shipped.Sum(s => s.BaseQty), received.SelectMany(r => r.Allocations).Sum(a => a.BaseQty));
    }

    [Fact]
    public void Receipt_covers_in_expiry_order_but_keeps_input_line_order()
    {
        StockAllocation[] shipped = [A(null, "2"), A("2027-03-01", "2"), A("2027-01-01", "3")];

        var received = StockTransfer.AllocateReceipt([B(null, "-4")], shipped, trackExpiry: true);

        Assert.Equal(shipped, received.Select(r => r.Shipped));
        Assert.Equal([A(null, "2")], received[0].Allocations);                         // nothing left to cover
        Assert.Equal([A(null, "1"), A("2027-03-01", "1")], received[1].Allocations);   // covers the last 1
        Assert.Equal([A(null, "3")], received[2].Allocations);                         // earliest covers first
    }

    [Fact]
    public void Receipt_for_untracked_product_goes_to_null_bucket()
    {
        var received = StockTransfer.AllocateReceipt([], [A("2027-01-01", "3"), A(null, "1")], trackExpiry: false);

        Assert.Equal([A(null, "3")], received[0].Allocations);
        Assert.Equal([A(null, "1")], received[1].Allocations);
    }

    [Fact]
    public void Receipt_of_zero_qty_line_has_no_allocations()
    {
        var received = StockTransfer.AllocateReceipt([B(null, "-4")], [A("2027-01-01", "0")], trackExpiry: true);

        Assert.Empty(Assert.Single(received).Allocations);
    }

    [Fact]
    public void Receipt_of_no_lines_is_empty()
    {
        Assert.Empty(StockTransfer.AllocateReceipt([B(null, "-4")], [], trackExpiry: true));
    }

    // ---- invariants -----------------------------------------------------------------------

    public static TheoryData<int> Seeds => new() { 1, 2, 3, 42, 2026 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Transfer_invariants_hold_over_random_transfers(int seed)
    {
        var random = new Random(seed);
        Guid[] locations = [From, To, Guid.CreateVersion7()];
        DateOnly?[] expiries = [null, Day("2026-11-01"), Day("2026-11-20"), Day("2027-01-01"), Day("2027-06-30")];

        IReadOnlyDictionary<StockBalanceKey, decimal> balances = locations
            .SelectMany(l => expiries.Select(e => (Key: new StockBalanceKey(l, Product, e), Qty: (decimal)random.Next(-3, 8))))
            .Where(x => x.Key.ExpiryDate is null || x.Qty >= 0)
            .ToDictionary(x => x.Key, x => x.Qty);

        for (var step = 0; step < 300; step++)
        {
            var from = locations[random.Next(locations.Length)];
            var to = locations.Where(l => l != from).ElementAt(random.Next(locations.Length - 1));
            var qty = random.Next(4) == 0 ? random.Next(1, 4) * 0.25m : random.Next(0, 12);
            var trackExpiry = random.Next(3) != 0;
            var request = new StockTransferRequest(Node, Product, from, to, qty, Guid.CreateVersion7(), null, At);

            var result = StockTransfer.Transfer(request, balances, Today, trackExpiry, allowExpired: random.Next(4) == 0);
            var context = $"seed={seed} step={step} qty={qty} track={trackExpiry}";

            var totalBefore = balances.Values.Sum();
            balances = StockBalances.Apply(balances, result.All);

            Assert.True(-result.Out.Sum(e => e.BaseQty) == qty, "out == qty: " + context);
            Assert.True(result.In.Sum(e => e.BaseQty) == qty, "in == qty: " + context);
            Assert.True(balances.Values.Sum() == totalBefore, "total unchanged: " + context);
            Assert.All(balances.Where(b => b.Key.ExpiryDate is not null),
                b => Assert.True(b.Value >= 0, $"dated bucket negative {b.Key}: {context}"));
        }
    }

    // ---- per expiry (one location_transfer_lines row each) --------------------------------

    [Fact]
    public void By_expiry_gives_one_line_per_fefo_allocation_with_its_own_ledger_rows()
    {
        var balances = Balances([(From, "2027-03-01", "5"), (From, "2027-01-01", "3")]);
        var ids = new Queue<Guid>(Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()));

        var lines = StockTransfer.TransferByExpiry(Request("5"), balances, Today, trackExpiry: true, newId: ids.Dequeue);

        Assert.Equal([(Day("2027-01-01"), 3m), (Day("2027-03-01"), 2m)], lines.Select(l => (l.ExpiryDate, l.BaseQty)));
        Assert.All(lines, l =>
        {
            Assert.All(l.Out.Concat(l.In), e => Assert.Equal(l.LineId, e.DocLineId));      // ledger points at its line
            Assert.Equal(l.BaseQty, -l.Out.Sum(e => e.BaseQty));
            Assert.Equal(l.BaseQty, l.In.Sum(e => e.BaseQty));
            Assert.All(l.Out, e => Assert.Equal(From, e.LocationId));
            Assert.All(l.In, e => Assert.Equal(To, e.LocationId));
        });
        Assert.Equal(2, lines.Select(l => l.LineId).Distinct().Count());
    }

    [Fact]
    public void By_expiry_matches_transfer_and_keeps_totals()
    {
        var balances = Balances([(From, "2027-01-01", "3"), (From, "2027-03-01", "5"), (To, null, "-2")]);

        var lines = StockTransfer.TransferByExpiry(Request("10"), balances, Today, trackExpiry: true);
        var plain = StockTransfer.Transfer(Request("10"), balances, Today, trackExpiry: true);

        Assert.Equal(Shape(plain.Out), Shape(lines.SelectMany(l => l.Out)));
        Assert.Equal(Shape(plain.In).OrderBy(x => x.Item1 ?? DateOnly.MaxValue).ThenBy(x => x.Item2),
            Shape(lines.SelectMany(l => l.In)).OrderBy(x => x.Item1 ?? DateOnly.MaxValue).ThenBy(x => x.Item2));
        Assert.Equal(10m, lines.Sum(l => l.BaseQty));                                         // 3 + 5 dated, 2 from the null bucket (negative)
        var after = StockBalances.Apply(balances, lines.SelectMany(l => l.Out.Concat(l.In)));
        Assert.Equal(-2m, after[new(From, Product, null)]);                                  // shortage allowed: source goes negative
        Assert.Equal(balances.Values.Sum(), after.Values.Sum());                              // nothing created or lost
    }

    [Fact]
    public void By_expiry_skips_expired_stock_like_a_sale()
    {
        var balances = Balances([(From, "2026-11-01", "4"), (From, "2027-01-01", "3")]);

        var lines = StockTransfer.TransferByExpiry(Request("3"), balances, Today, trackExpiry: true);

        Assert.Equal([(Day("2027-01-01"), 3m)], lines.Select(l => (l.ExpiryDate, l.BaseQty)));
    }
}
