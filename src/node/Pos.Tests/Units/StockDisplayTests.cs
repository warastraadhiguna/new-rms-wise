using Pos.Shared.Units;

namespace Pos.Tests.Units;

public class StockDisplayTests
{
    private static string Box(string baseQty) =>
        StockDisplay.Format(D(baseQty), "BOX", 24m, "PCS", baseAllowsDecimal: false);

    [Theory]
    [InlineData("38", "1 BOX 14 PCS")]
    [InlineData("48", "2 BOX")]               // exact multiple: no "0 PCS"
    [InlineData("10", "10 PCS")]              // less than one box: no "0 BOX"
    [InlineData("24", "1 BOX")]
    [InlineData("0", "0")]
    [InlineData("24000", "1.000 BOX")]        // thousands separator on the unit count
    [InlineData("24023", "1.000 BOX 23 PCS")]
    public void Larger_unit_shows_whole_units_and_the_rest_in_the_base_unit(string baseQty, string expected) =>
        Assert.Equal(expected, Box(baseQty));

    [Theory]
    [InlineData("-26", "-1 BOX 2 PCS")]
    [InlineData("-48", "-2 BOX")]
    [InlineData("-5", "-5 PCS")]
    public void Negative_stock_puts_one_minus_sign_in_front(string baseQty, string expected) =>
        Assert.Equal(expected, Box(baseQty));

    [Theory]
    [InlineData("38", "38")]
    [InlineData("1234", "1.234")]
    [InlineData("-7", "-7")]
    [InlineData("0", "0")]
    public void Base_unit_is_a_plain_number(string baseQty, string expected) =>
        Assert.Equal(expected, StockDisplay.Format(D(baseQty), "PCS", 1m, "PCS", baseAllowsDecimal: false));

    [Fact]
    public void Another_unit_with_conversion_one_is_also_a_plain_number() =>
        Assert.Equal("12", StockDisplay.Format(12m, "BUAH", 1m, "PCS", baseAllowsDecimal: false));

    [Theory]
    [InlineData("2.345", "2,35")]             // at most 2 decimals, half away from zero
    [InlineData("2.5", "2,5")]
    [InlineData("3", "3")]
    [InlineData("-0.125", "-0,13")]
    [InlineData("-0.004", "0")]               // rounds to zero: no "-0"
    public void Decimal_base_unit_shows_at_most_two_decimals(string baseQty, string expected) =>
        Assert.Equal(expected, StockDisplay.Format(D(baseQty), "KG", 1m, "KG", baseAllowsDecimal: true));

    [Theory]
    [InlineData("37.456", "1 SAK 12,46 KG")]
    [InlineData("24.996", "1 SAK")]           // rounded first, so never "0 SAK 25 KG"
    [InlineData("-30.5", "-1 SAK 5,5 KG")]
    [InlineData("0.5", "0,5 KG")]
    public void Decimal_base_unit_in_a_larger_unit(string baseQty, string expected) =>
        Assert.Equal(expected, StockDisplay.Format(D(baseQty), "SAK", 25m, "KG", baseAllowsDecimal: true));

    [Fact]
    public void Whole_number_base_unit_keeps_unexpected_fractions_instead_of_hiding_them() =>
        Assert.Equal("1 BOX 0,5 PCS", Box("24.5"));

    [Fact]
    public void Fractional_conversion_counts_whole_units() =>
        Assert.Equal("2 PAK 0,5 KG", StockDisplay.Format(5.5m, "PAK", 2.5m, "KG", baseAllowsDecimal: true));

    [Fact]
    public void Split_returns_the_parts()
    {
        var split = StockDisplay.Split(-38m, 24m, baseAllowsDecimal: false);

        Assert.True(split.IsNegative);
        Assert.Equal(1m, split.Units);
        Assert.Equal(14m, split.Remainder);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Invalid_conversion_falls_back_to_the_base_unit_number(string conversion) =>
        Assert.Equal("38", StockDisplay.Format(38m, "BOX", D(conversion), "PCS", baseAllowsDecimal: false));
}
