using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class PriceResolverTests
{
    private static readonly Guid Branch = Guid.Parse("00000000-0000-7000-8000-0000000000b1");
    private static readonly Guid OtherBranch = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly DateTime Now = Utc("2026-11-20T03:00:00Z");

    private static PriceCandidate P(string price, string effectiveFrom, Guid? branch = null, string minQty = "1") =>
        new(branch, D(price), D(minQty), Utc(effectiveFrom));

    private static decimal? Resolve(decimal qty, params PriceCandidate[] candidates) =>
        PriceResolver.Resolve(candidates, Branch, Now, qty);

    [Fact]
    public void Default_price_when_no_branch_row()
    {
        Assert.Equal(4000m, Resolve(1m, P("4000", "2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Branch_row_wins_over_default()
    {
        Assert.Equal(18000m, Resolve(1m,
            P("17500", "2026-06-01T00:00:00Z"),
            P("18000", "2026-01-01T00:00:00Z", Branch)));
    }

    [Fact]
    public void Other_branch_rows_are_ignored()
    {
        Assert.Equal(17500m, Resolve(1m,
            P("17500", "2026-01-01T00:00:00Z"),
            P("19000", "2026-01-01T00:00:00Z", OtherBranch)));
    }

    [Fact]
    public void Latest_effective_price_not_in_the_future()
    {
        Assert.Equal(4200m, Resolve(1m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("4200", "2026-11-01T00:00:00Z"),
            P("4500", "2026-12-01T00:00:00Z")));      // not effective yet
    }

    [Fact]
    public void Price_effective_exactly_now_applies()
    {
        Assert.Equal(4200m, Resolve(1m, P("4000", "2026-01-01T00:00:00Z"), P("4200", "2026-11-20T03:00:00Z")));
    }

    [Theory]
    [InlineData("1", "4000")]
    [InlineData("11", "4000")]
    [InlineData("12", "3500")]       // wholesale tier reached
    [InlineData("24", "3200")]       // largest min_qty <= qty
    [InlineData("100", "3200")]
    public void Wholesale_tier_with_largest_min_qty_not_above_qty(string qty, string expected)
    {
        Assert.Equal(D(expected), Resolve(D(qty),
            P("4000", "2026-01-01T00:00:00Z"),
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3200", "2026-01-01T00:00:00Z", minQty: "24")));
    }

    [Fact]
    public void Each_tier_uses_its_own_latest_price()
    {
        Assert.Equal(3400m, Resolve(12m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3400", "2026-10-01T00:00:00Z", minQty: "12")));
    }

    [Fact]
    public void Default_wholesale_tier_beats_branch_base_tier()
    {
        // branch only has the base tier (min 1); the default tier for 12+ has the larger min_qty
        Assert.Equal(3500m, Resolve(12m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("4100", "2026-01-01T00:00:00Z", Branch)));
    }

    [Fact]
    public void Branch_base_tier_applies_below_the_default_wholesale_tier()
    {
        Assert.Equal(4100m, Resolve(11m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("4100", "2026-01-01T00:00:00Z", Branch)));
    }

    [Fact]
    public void Same_min_qty_branch_wins_even_when_more_expensive()
    {
        Assert.Equal(3600m, Resolve(12m,
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3600", "2026-01-01T00:00:00Z", Branch, minQty: "12")));
    }

    [Fact]
    public void Branch_wholesale_tier_beats_smaller_default_tier()
    {
        Assert.Equal(3300m, Resolve(24m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3300", "2026-01-01T00:00:00Z", Branch, minQty: "24")));
    }

    [Fact]
    public void Larger_default_tier_beats_smaller_branch_tier()
    {
        Assert.Equal(3200m, Resolve(30m,
            P("3200", "2026-01-01T00:00:00Z", minQty: "24"),
            P("3400", "2026-01-01T00:00:00Z", Branch, minQty: "12")));
    }

    [Fact]
    public void Future_branch_tier_is_ignored()
    {
        Assert.Equal(3500m, Resolve(12m,
            P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3300", "2027-01-01T00:00:00Z", Branch, minQty: "12")));
    }

    [Fact]
    public void Default_set_is_used_when_branch_has_no_price_for_the_qty()
    {
        // branch defines only a wholesale tier; small quantities fall back to the default list
        Assert.Equal(4000m, Resolve(2m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3300", "2026-01-01T00:00:00Z", Branch, minQty: "12")));
    }

    [Fact]
    public void Qty_below_the_smallest_tier_uses_the_smallest_tier()
    {
        // 0,5 kg rice with a price from 1 kg
        Assert.Equal(14000m, Resolve(0.5m, P("14000", "2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Below_every_tier_the_smallest_min_qty_wins_over_a_branch_wholesale_tier()
    {
        // branch only has a 12+ tier, default has the 1+ tier: 0,5 must not get the wholesale price
        Assert.Equal(4000m, Resolve(0.5m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("3300", "2026-01-01T00:00:00Z", Branch, minQty: "12")));
    }

    [Fact]
    public void Below_every_tier_branch_wins_on_equal_smallest_min_qty()
    {
        Assert.Equal(4100m, Resolve(0.5m,
            P("4000", "2026-01-01T00:00:00Z"),
            P("4100", "2026-01-01T00:00:00Z", Branch)));
    }

    [Fact]
    public void Only_wholesale_tiers_still_price_small_quantities()
    {
        Assert.Equal(3500m, Resolve(2m, P("3500", "2026-01-01T00:00:00Z", minQty: "12")));
    }

    [Fact]
    public void Decimal_tiers_for_weighed_goods()
    {
        Assert.Equal(14000m, Resolve(0.5m, P("14000", "2026-01-01T00:00:00Z", minQty: "0.1")));
    }

    [Fact]
    public void No_candidates_no_price()
    {
        Assert.Null(Resolve(1m));
    }

    [Fact]
    public void Only_future_prices_no_price()
    {
        Assert.Null(Resolve(1m, P("4000", "2027-01-01T00:00:00Z")));
    }

    [Fact]
    public void Same_tier_and_time_takes_the_lower_price()
    {
        Assert.Equal(3900m, Resolve(1m, P("4000", "2026-01-01T00:00:00Z"), P("3900", "2026-01-01T00:00:00Z")));
    }


    // ---- setting pricing.uniform = true: one price for every branch --------------------------------------

    [Fact]
    public void Uniform_pricing_ignores_the_branch_rows()
    {
        PriceCandidate[] rows = [P("17500", "2026-06-01T00:00:00Z"), P("18000", "2026-01-01T00:00:00Z", Branch)];

        Assert.Equal(18000m, PriceResolver.Resolve(rows, Branch, Now, 1m));
        Assert.Equal(17500m, PriceResolver.Resolve(rows, Branch, Now, 1m, uniformPricing: true));
    }

    [Fact]
    public void Uniform_pricing_ignores_branch_wholesale_tiers_too()
    {
        PriceCandidate[] rows =
        [
            P("4000", "2026-01-01T00:00:00Z"), P("3500", "2026-01-01T00:00:00Z", minQty: "12"),
            P("3000", "2026-01-01T00:00:00Z", Branch, minQty: "6"),
        ];

        Assert.Equal(3000m, PriceResolver.Resolve(rows, Branch, Now, 6m));
        Assert.Equal(4000m, PriceResolver.Resolve(rows, Branch, Now, 6m, uniformPricing: true));
        Assert.Equal(3500m, PriceResolver.Resolve(rows, Branch, Now, 12m, uniformPricing: true));
    }

    [Fact]
    public void Uniform_pricing_with_only_a_branch_price_has_no_price()
    {
        Assert.Null(PriceResolver.Resolve([P("18000", "2026-01-01T00:00:00Z", Branch)], Branch, Now, 1m, uniformPricing: true));
    }
    [Fact]
    public void Rejects_non_positive_qty_and_non_utc_now()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceResolver.Resolve([], Branch, Now, 0m));
        Assert.Throws<ArgumentException>(() =>
            PriceResolver.Resolve([], Branch, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Local), 1m));
    }
}
