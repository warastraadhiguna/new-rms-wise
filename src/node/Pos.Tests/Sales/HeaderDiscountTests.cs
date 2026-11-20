using Pos.Shared.Auth;
using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class HeaderDiscountTests
{
    private static readonly CashRoundingSettings Round100 = new(100m, CashRoundingMode.Nearest);

    private static PaymentInput Cash(string amount) => new(PaymentKind.Cash, D(amount));
    private static PaymentInput Qris(string amount) => new(PaymentKind.NonCash, D(amount));

    private static SaleTotals Calc(decimal[] lines, PaymentInput[] payments, HeaderDiscount discount, int decimals = 0) =>
        SaleCalculator.Calculate(lines, payments, Round100, hasCustomer: false, discount, decimals);

    [Fact]
    public void Percent_discount_after_line_totals()
    {
        // 20000 + 5000 = 25000, 10% = 2500 -> 22500
        var t = Calc([20000m, 5000m], [Qris("22500")], HeaderDiscount.Percent(10m));

        Assert.Equal(25000m, t.Subtotal);
        Assert.Equal(2500m, t.DiscountAmount);
        Assert.Equal(22500m, t.Total);
    }

    [Fact]
    public void Nominal_discount()
    {
        var t = Calc([25000m], [Qris("24000")], HeaderDiscount.Nominal(1000m));

        Assert.Equal(1000m, t.DiscountAmount);
        Assert.Equal(24000m, t.Total);
    }

    [Fact]
    public void Discount_is_rounded_to_currency_precision()
    {
        // 12345 x 2.5% = 308.625 -> 309
        var t = Calc([12345m], [Qris("12036")], HeaderDiscount.Percent(2.5m));
        Assert.Equal(309m, t.DiscountAmount);

        // with 2 decimals: 308.63
        var t2 = Calc([12345m], [Qris("12036.37")], HeaderDiscount.Percent(2.5m), decimals: 2);
        Assert.Equal(308.63m, t2.DiscountAmount);
    }

    [Fact]
    public void Discount_never_exceeds_subtotal()
    {
        var t = Calc([5000m], [], HeaderDiscount.Nominal(7000m));

        Assert.Equal(5000m, t.DiscountAmount);
        Assert.Equal(0m, t.Total);
    }

    [Fact]
    public void Cash_rounding_applies_after_the_discount()
    {
        // 12345 - 10% (1235 after rounding 1234.5) = 11110 -> 11100 with cash
        var t = Calc([12345m], [Cash("20000")], HeaderDiscount.Percent(10m));

        Assert.Equal(1235m, t.DiscountAmount);
        Assert.Equal(-10m, t.RoundingAmount);
        Assert.Equal(11100m, t.Total);
        Assert.Equal(t.Subtotal - t.DiscountAmount + t.RoundingAmount, t.Total);
    }

    [Fact]
    public void No_discount_by_default()
    {
        var t = SaleCalculator.Calculate([10000m], [Qris("10000")], Round100, hasCustomer: false);

        Assert.Equal(0m, t.DiscountAmount);
        Assert.Equal(10000m, t.Total);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100.01")]
    public void Percent_outside_0_to_100_is_rejected(string pct)
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([1000m], [], HeaderDiscount.Percent(D(pct))));
        Assert.Equal("invalid_discount", ex.Code);
    }

    [Fact]
    public void Negative_nominal_is_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([1000m], [], HeaderDiscount.Nominal(-1m)));
        Assert.Equal("invalid_discount", ex.Code);
    }

    [Fact]
    public void Percent_and_nominal_together_are_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => HeaderDiscount.From(5m, 1000m));
        Assert.Equal("invalid_discount", ex.Code);
    }

    [Fact]
    public void From_request_values()
    {
        Assert.Equal(HeaderDiscount.None, HeaderDiscount.From(null, null));
        Assert.Equal(HeaderDiscount.None, HeaderDiscount.From(0m, 0m));
        Assert.Equal(HeaderDiscount.Percent(5m), HeaderDiscount.From(5m, null));
        Assert.Equal(HeaderDiscount.Nominal(1000m), HeaderDiscount.From(null, 1000m));
    }
}

public class DiscountPolicyTests
{
    [Theory]
    [InlineData(new[] { "discount.max:10" }, "10")]
    [InlineData(new[] { "discount.max:5", "discount.max:12.5" }, "12.5")]
    [InlineData(new[] { "sale.create" }, "0")]
    [InlineData(new string[0], "0")]
    public void Max_percent_comes_from_permissions(string[] permissions, string expected)
    {
        Assert.Equal(D(expected), DiscountPolicy.MaxPercent(PermissionSet.FromNames(permissions)));
    }

    [Fact]
    public void Wildcard_has_no_limit()
    {
        Assert.Null(DiscountPolicy.MaxPercent(PermissionSet.FromNames(["*"])));
    }

    [Fact]
    public void Malformed_limit_is_ignored()
    {
        Assert.Equal(10m, DiscountPolicy.MaxPercent(PermissionSet.FromNames(["discount.max:abc", "discount.max:10"])));
    }

    [Theory]
    [InlineData("12345", "1235", "10", false)]   // 10% rounded up still counts as 10%
    [InlineData("12345", "1236", "10", true)]
    [InlineData("10000", "1000", "10", false)]
    [InlineData("10000", "1001", "10", true)]
    [InlineData("10000", "1", "0", true)]        // no discount right at all
    [InlineData("10000", "0", "0", false)]
    [InlineData("0", "0", "0", false)]
    public void Approval_needed_above_the_limit(string subtotal, string discount, string maxPct, bool expected)
    {
        Assert.Equal(expected, DiscountPolicy.RequiresApproval(D(discount), D(subtotal), D(maxPct), currencyDecimals: 0));
    }

    [Fact]
    public void Unlimited_never_needs_approval()
    {
        Assert.False(DiscountPolicy.RequiresApproval(10000m, 10000m, null, 0));
    }
}
