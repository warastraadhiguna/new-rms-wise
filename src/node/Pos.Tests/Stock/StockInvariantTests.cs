using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

/// <summary>
/// Randomised (fixed seed) sequences of inbound and outbound movements that check the stock invariants
/// after every step.
/// </summary>
public class StockInvariantTests
{
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly Guid Location = Guid.CreateVersion7();
    private static readonly Guid Product = Guid.CreateVersion7();
    private static readonly DateOnly Today = Day("2026-11-20");

    private static readonly DateOnly?[] Expiries =
        [null, Day("2026-11-01"), Day("2026-11-19"), Day("2026-11-20"), Day("2026-12-31"), Day("2027-01-01"), Day("2027-06-30")];

    public const string Tracked = "tracked";
    public const string Untracked = "untracked";
    public const string SwitchedOff = "switched-off";   // tracked for the first half, untracked afterwards

    public static TheoryData<int, string> Seeds()
    {
        var data = new TheoryData<int, string>();
        foreach (var seed in new[] { 1, 2, 3, 7, 42, 2026 })
        foreach (var mode in new[] { Tracked, Untracked, SwitchedOff })
            data.Add(seed, mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Invariants_hold_over_random_movements(int seed, string mode)
    {
        var random = new Random(seed);
        IReadOnlyDictionary<StockBalanceKey, decimal> balances = new Dictionary<StockBalanceKey, decimal>();

        for (var step = 0; step < 300; step++)
        {
            var trackExpiry = mode == Tracked || (mode == SwitchedOff && step < 150);
            var datedKeysBefore = balances.Keys.Where(k => k.ExpiryDate is not null).ToHashSet();
            var qty = RandomQty(random);
            var inbound = random.Next(2) == 0;
            var allowExpired = random.Next(4) == 0;
            var expiry = Expiries[random.Next(Expiries.Length)];
            var buckets = StockBalances.BucketsFor(balances, Location, Product);

            IReadOnlyList<StockAllocation> Allocate(IReadOnlyList<StockBucket> input) => inbound
                ? StockAllocator.AllocateInbound(input, qty, expiry, trackExpiry)
                : StockAllocator.AllocateOutbound(input, qty, trackExpiry, Today, allowExpired);

            var allocations = Allocate(buckets);

            var context = $"seed={seed} step={step} {(inbound ? "in" : "out")} qty={qty}";

            // allocated == requested / received
            Assert.True(allocations.Sum(a => a.BaseQty) == qty, "allocated total: " + context);
            Assert.All(allocations, a => Assert.True(a.BaseQty > 0, "positive allocation: " + context));
            Assert.Equal(allocations.Count, allocations.Select(a => a.ExpiryDate).Distinct().Count());

            // deterministic: same input (any order) -> same result
            Assert.Equal(allocations, Allocate(buckets.OrderBy(_ => random.Next()).ToArray()));

            var line = new StockDocumentLine(Node, Location, Product,
                inbound ? StockDocTypes.GoodsReceipt : StockDocTypes.Sale,
                Guid.CreateVersion7(), Guid.CreateVersion7(), Utc("2026-11-20T03:00:00Z"));
            var entries = StockLedgerBuilder.Build(line, inbound ? StockDirection.In : StockDirection.Out, allocations);

            var before = balances.Values.Sum();
            balances = StockBalances.Apply(balances, entries);

            // total after == total before + entries; sign matches direction
            Assert.True(balances.Values.Sum() == before + entries.Sum(e => e.BaseQty), "total: " + context);
            Assert.True(entries.Sum(e => e.BaseQty) == (inbound ? qty : -qty), "signed total: " + context);

            // only the null bucket may go negative
            Assert.All(balances.Where(b => b.Key.ExpiryDate is not null),
                b => Assert.True(b.Value >= 0, $"dated bucket {b.Key.ExpiryDate} = {b.Value}: {context}"));

            // untracked: no new dated buckets; inbound never adds to dated buckets
            if (!trackExpiry)
            {
                Assert.Subset(datedKeysBefore, balances.Keys.Where(k => k.ExpiryDate is not null).ToHashSet());
                if (inbound)
                    Assert.All(entries, e => Assert.Null(e.ExpiryDate));
            }
        }
    }

    private static decimal RandomQty(Random random) => random.Next(5) switch
    {
        0 => 0m,
        1 => random.Next(1, 4) * 0.25m,
        _ => random.Next(1, 20),
    };
}
