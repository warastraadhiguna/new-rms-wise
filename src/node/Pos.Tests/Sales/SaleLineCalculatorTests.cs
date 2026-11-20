using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class SaleLineCalculatorTests
{
    [Fact]
    public void No_discount_line_total_is_qty_times_price()
    {
        var r = SaleLineCalculator.Calculate(qty: 3m, unitPrice: 12500m);

        Assert.Equal(37500m, r.Gross);
        Assert.Equal(0m, r.DiscountAmount);
        Assert.Equal(37500m, r.LineTotal);
    }

    [Fact]
    public void Percent_discount_only()
    {
        var r = SaleLineCalculator.Calculate(qty: 2m, unitPrice: 10000m, discountPct: 10m);

        Assert.Equal(20000m, r.Gross);
        Assert.Equal(2000m, r.DiscountAmount);
        Assert.Equal(18000m, r.LineTotal);
    }

    [Fact]
    public void Nominal_discount_only_is_per_line()
    {
        var r = SaleLineCalculator.Calculate(qty: 2m, unitPrice: 10000m, discountAmount: 1500m);

        Assert.Equal(1500m, r.DiscountAmount);
        Assert.Equal(18500m, r.LineTotal);
    }

    [Fact]
    public void Percent_is_applied_first_then_nominal()
    {
        // 20000 - 10% = 18000, then - 500 = 17500
        var r = SaleLineCalculator.Calculate(qty: 2m, unitPrice: 10000m, discountPct: 10m, discountAmount: 500m);

        Assert.Equal(2500m, r.DiscountAmount);
        Assert.Equal(17500m, r.LineTotal);
    }

    [Fact]
    public void Decimal_qty_and_price()
    {
        // 0.25 KG x 48000 = 12000
        var r = SaleLineCalculator.Calculate(qty: 0.25m, unitPrice: 48000m);
        Assert.Equal(12000m, r.LineTotal);
    }

    [Fact]
    public void Discount_and_total_are_rounded_to_currency_decimals_by_default()
    {
        // gross 1 x 333.33 -> 333 (unit price keeps 4 dp, gross is currency precision)
        // 12.5% of 333 = 41.625 -> 42 ; 333 - 42 = 291
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 333.33m, discountPct: 12.5m);

        Assert.Equal(333m, r.Gross);
        Assert.Equal(42m, r.DiscountAmount);
        Assert.Equal(291m, r.LineTotal);
    }

    [Fact]
    public void Gross_is_rounded_to_currency_decimals_before_discount()
    {
        // 0.25 KG x 33333.33 = 8333.3325 -> 8333
        var r = SaleLineCalculator.Calculate(qty: 0.25m, unitPrice: 33333.33m);

        Assert.Equal(8333m, r.Gross);
        Assert.Equal(8333m, r.LineTotal);
    }

    [Fact]
    public void Percent_discount_is_computed_from_rounded_gross()
    {
        // 3 x 1666.5 = 4999.5 -> gross 5000
        // 0.01% of 5000 = 0.5 -> 1 (from the unrounded 4999.5 it would be 0.49995 -> 0)
        var r = SaleLineCalculator.Calculate(qty: 3m, unitPrice: 1666.5m, discountPct: 0.01m);

        Assert.Equal(5000m, r.Gross);
        Assert.Equal(1m, r.DiscountAmount);
        Assert.Equal(4999m, r.LineTotal);
    }

    [Fact]
    public void Nominal_discount_is_rounded_to_currency_decimals()
    {
        // 10000 - 1500.5 -> discount 1501, total 8499
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 10000m, discountAmount: 1500.5m);

        Assert.Equal(1501m, r.DiscountAmount);
        Assert.Equal(8499m, r.LineTotal);
    }

    [Fact]
    public void Currency_decimals_two()
    {
        // 12.5% of 333.33 = 41.66625 -> 41.67 ; 333.33 - 41.67 = 291.66
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 333.33m, discountPct: 12.5m, currencyDecimals: 2);

        Assert.Equal(41.67m, r.DiscountAmount);
        Assert.Equal(291.66m, r.LineTotal);
    }

    [Fact]
    public void Currency_decimals_four_keeps_storage_precision()
    {
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 333.33m, discountPct: 12.5m, currencyDecimals: 4);

        Assert.Equal(41.6663m, r.DiscountAmount);
        Assert.Equal(291.6637m, r.LineTotal);
    }

    [Fact]
    public void Rounding_never_makes_line_total_negative()
    {
        // gross 0.6 -> 1, discount 0.6 -> 1 -> total 0
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 0.6m, discountAmount: 0.6m);

        Assert.Equal(1m, r.Gross);
        Assert.Equal(1m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    [Fact]
    public void Gross_below_half_unit_rounds_to_zero()
    {
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 0.4m, discountAmount: 0.4m);

        Assert.Equal(0m, r.Gross);
        Assert.Equal(0m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    public static TheoryData<int> CurrencyDecimals => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(CurrencyDecimals))]
    public void Invariants_hold_for_many_combinations(int currencyDecimals)
    {
        decimal[] qtys = [0.0001m, 0.25m, 0.3333m, 1m, 1.5m, 3m, 7m, 24m, 999.9999m];
        decimal[] prices = [0m, 0.0001m, 0.4m, 0.5m, 0.6m, 333.33m, 1666.5m, 12345.6789m, 99999.9999m];
        decimal[] pcts = [0m, 0.01m, 12.5m, 33.3333m, 50m, 99.99m, 100m];
        decimal[] nominals = [0m, 0.4m, 0.5m, 0.6m, 1500.5m, 1_000_000m];

        foreach (var qty in qtys)
        foreach (var price in prices)
        foreach (var pct in pcts)
        foreach (var nominal in nominals)
        {
            var r = SaleLineCalculator.Calculate(qty, price, pct, nominal, currencyDecimals);
            var context = $"qty={qty} price={price} pct={pct} nominal={nominal} dp={currencyDecimals} -> {r}";

            Assert.True(r.LineTotal == r.Gross - r.DiscountAmount, "LineTotal == Gross - DiscountAmount: " + context);
            Assert.True(r.DiscountAmount <= r.Gross, "DiscountAmount <= Gross: " + context);
            Assert.True(r.LineTotal >= 0, "LineTotal >= 0: " + context);
            Assert.True(r.DiscountAmount >= 0, "DiscountAmount >= 0: " + context);
            Assert.True(IsAtPrecision(r.Gross, currencyDecimals), "Gross precision: " + context);
            Assert.True(IsAtPrecision(r.DiscountAmount, currencyDecimals), "DiscountAmount precision: " + context);
            Assert.True(IsAtPrecision(r.LineTotal, currencyDecimals), "LineTotal precision: " + context);
        }
    }

    private static bool IsAtPrecision(decimal value, int decimals) => decimal.Round(value, decimals) == value;

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Rejects_currency_decimals_outside_0_to_4(int decimals)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SaleLineCalculator.Calculate(qty: 1m, unitPrice: 1000m, currencyDecimals: decimals));
    }

    [Fact]
    public void Gross_at_four_currency_decimals_matches_storage_scale()
    {
        // 0.3333 x 0.3333 = 0.11108889 -> 0.1111
        var r = SaleLineCalculator.Calculate(qty: 0.3333m, unitPrice: 0.3333m, currencyDecimals: 4);
        Assert.Equal(0.1111m, r.Gross);
    }

    [Fact]
    public void Nominal_discount_larger_than_gross_is_capped_and_total_is_zero()
    {
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 5000m, discountAmount: 7000m);

        Assert.Equal(5000m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    [Fact]
    public void Combined_discount_larger_than_gross_is_capped()
    {
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 5000m, discountPct: 50m, discountAmount: 3000m);

        Assert.Equal(5000m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    [Fact]
    public void Hundred_percent_discount_gives_zero()
    {
        var r = SaleLineCalculator.Calculate(qty: 3m, unitPrice: 7000m, discountPct: 100m);

        Assert.Equal(21000m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    [Fact]
    public void Zero_price_is_allowed()
    {
        var r = SaleLineCalculator.Calculate(qty: 1m, unitPrice: 0m, discountAmount: 100m);

        Assert.Equal(0m, r.DiscountAmount);
        Assert.Equal(0m, r.LineTotal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Rejects_non_positive_qty(string qty)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SaleLineCalculator.Calculate(qty: D(qty), unitPrice: 1000m));
    }

    [Fact]
    public void Rejects_negative_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SaleLineCalculator.Calculate(qty: 1m, unitPrice: -1m));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public void Rejects_percent_outside_0_to_100(string pct)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SaleLineCalculator.Calculate(qty: 1m, unitPrice: 1000m, discountPct: D(pct)));
    }

    [Fact]
    public void Rejects_negative_nominal_discount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SaleLineCalculator.Calculate(qty: 1m, unitPrice: 1000m, discountAmount: -1m));
    }
}
