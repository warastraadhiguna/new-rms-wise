using Pos.Shared.Units;

namespace Pos.Tests.Units;

public class UnitConversionTests
{
    [Theory]
    [InlineData("2", "24", "48")]          // 2 BOX x 24 PCS
    [InlineData("1", "1", "1")]            // base unit
    [InlineData("0", "24", "0")]           // zero qty
    [InlineData("0.25", "1000", "250")]    // 0.25 KG -> 250 GR
    [InlineData("1.5", "0.5", "0.75")]     // fractional conversion
    [InlineData("-3", "12", "-36")]        // negative qty (adjustments) keeps sign
    public void ToBaseQty_multiplies_by_conversion(string qty, string conversion, string expected)
    {
        var result = UnitConversion.ToBaseQty(D(qty), D(conversion), allowDecimal: true);
        Assert.Equal(D(expected), result);
    }

    [Fact]
    public void ToBaseQty_accepts_whole_qty_when_decimal_not_allowed()
    {
        Assert.Equal(48m, UnitConversion.ToBaseQty(2.0000m, 24m, allowDecimal: false));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("0.0001")]
    [InlineData("-0.5")]
    public void ToBaseQty_rejects_fractional_qty_when_unit_disallows_decimal(string qty)
    {
        Assert.Throws<InvalidQuantityException>(
            () => UnitConversion.ToBaseQty(D(qty), 24m, allowDecimal: false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToBaseQty_rejects_non_positive_conversion(string conversion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConversion.ToBaseQty(1m, D(conversion), allowDecimal: true));
    }

    [Fact]
    public void ToBaseQty_rejects_qty_with_more_than_four_decimals()
    {
        Assert.Throws<InvalidQuantityException>(
            () => UnitConversion.ToBaseQty(1.00001m, 1m, allowDecimal: true));
    }

    [Fact]
    public void ToBaseQty_rounds_result_to_storage_scale()
    {
        // 0.3333 x 0.3333 = 0.11108889 -> 0.1111
        Assert.Equal(0.1111m, UnitConversion.ToBaseQty(0.3333m, 0.3333m, allowDecimal: true));
    }

    [Theory]
    [InlineData("48", "24", "2")]
    [InlineData("0", "24", "0")]
    [InlineData("250", "1000", "0.25")]
    [InlineData("-36", "12", "-3")]
    public void FromBaseQty_divides_by_conversion(string baseQty, string conversion, string expected)
    {
        var result = UnitConversion.FromBaseQty(D(baseQty), D(conversion), allowDecimal: true);
        Assert.Equal(D(expected), result);
    }

    [Fact]
    public void FromBaseQty_rejects_result_that_is_not_whole_when_unit_disallows_decimal()
    {
        // 30 PCS is not a whole number of BOX(24)
        Assert.Throws<InvalidQuantityException>(
            () => UnitConversion.FromBaseQty(30m, 24m, allowDecimal: false));
    }

    [Fact]
    public void FromBaseQty_returns_whole_qty_when_decimal_not_allowed()
    {
        Assert.Equal(3m, UnitConversion.FromBaseQty(72m, 24m, allowDecimal: false));
    }

    [Fact]
    public void FromBaseQty_rounds_repeating_result_to_storage_scale()
    {
        // 1 / 3 = 0.3333...
        Assert.Equal(0.3333m, UnitConversion.FromBaseQty(1m, 3m, allowDecimal: true));
    }

    [Fact]
    public void FromBaseQty_rejects_non_positive_conversion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConversion.FromBaseQty(1m, 0m, allowDecimal: true));
    }

    [Fact]
    public void Round_trip_whole_units()
    {
        var baseQty = UnitConversion.ToBaseQty(5m, 12m, allowDecimal: false);
        Assert.Equal(5m, UnitConversion.FromBaseQty(baseQty, 12m, allowDecimal: false));
    }
}
