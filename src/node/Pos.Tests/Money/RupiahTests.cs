using Pos.Shared.Money;

namespace Pos.Tests.Money;

public class RupiahTests
{
    [Theory]
    [InlineData("1.23455", "1.2346")]     // midpoint away from zero
    [InlineData("1.23454", "1.2345")]
    [InlineData("-1.23455", "-1.2346")]
    [InlineData("0", "0")]
    public void Round_uses_storage_scale_of_four(string amount, string expected)
    {
        Assert.Equal(D(expected), Rupiah.Round(D(amount)));
    }

    [Theory]
    // total, step, mode, expected rounded total, expected rounding_amount
    [InlineData("12345", "100", CashRoundingMode.Down, "12300", "-45")]
    [InlineData("12345", "100", CashRoundingMode.Up, "12400", "55")]
    [InlineData("12345", "100", CashRoundingMode.Nearest, "12300", "-45")]
    [InlineData("12350", "100", CashRoundingMode.Nearest, "12400", "50")]   // midpoint goes up
    [InlineData("12351", "100", CashRoundingMode.Nearest, "12400", "49")]
    [InlineData("12249", "500", CashRoundingMode.Nearest, "12000", "-249")]
    [InlineData("12250", "500", CashRoundingMode.Nearest, "12500", "250")]
    [InlineData("12001", "500", CashRoundingMode.Up, "12500", "499")]
    [InlineData("12499", "500", CashRoundingMode.Down, "12000", "-499")]
    [InlineData("12345.6789", "100", CashRoundingMode.Down, "12300", "-45.6789")]
    [InlineData("12345.6789", "1", CashRoundingMode.Nearest, "12346", "0.3211")]
    public void RoundCash_rounds_to_step(string total, string step, CashRoundingMode mode, string rounded, string roundingAmount)
    {
        var result = Rupiah.RoundCash(D(total), D(step), mode);

        Assert.Equal(D(rounded), result.Total);
        Assert.Equal(D(roundingAmount), result.RoundingAmount);
        Assert.Equal(D(total) + result.RoundingAmount, result.Total);
    }

    [Theory]
    [InlineData(CashRoundingMode.Up)]
    [InlineData(CashRoundingMode.Down)]
    [InlineData(CashRoundingMode.Nearest)]
    public void RoundCash_leaves_exact_multiple_unchanged(CashRoundingMode mode)
    {
        var result = Rupiah.RoundCash(12500m, 500m, mode);
        Assert.Equal(12500m, result.Total);
        Assert.Equal(0m, result.RoundingAmount);
    }

    [Theory]
    [InlineData(CashRoundingMode.Up)]
    [InlineData(CashRoundingMode.Down)]
    [InlineData(CashRoundingMode.Nearest)]
    public void RoundCash_zero_total_stays_zero(CashRoundingMode mode)
    {
        var result = Rupiah.RoundCash(0m, 100m, mode);
        Assert.Equal(0m, result.Total);
        Assert.Equal(0m, result.RoundingAmount);
    }

    [Fact]
    public void RoundCash_step_zero_disables_rounding()
    {
        var result = Rupiah.RoundCash(12345.5m, 0m, CashRoundingMode.Nearest);
        Assert.Equal(12345.5m, result.Total);
        Assert.Equal(0m, result.RoundingAmount);
    }

    [Fact]
    public void RoundCash_rejects_negative_step()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Rupiah.RoundCash(12345m, -100m, CashRoundingMode.Nearest));
    }

    [Fact]
    public void RoundCash_rejects_undefined_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Rupiah.RoundCash(12345m, 100m, (CashRoundingMode)99));
    }

    [Theory]
    // negative totals (refunds): Up = toward +infinity, Down = toward -infinity,
    // so each mode always favours the same party; Nearest midpoint stays away from zero
    [InlineData("-12345", CashRoundingMode.Up, "-12300", "45")]
    [InlineData("-12350", CashRoundingMode.Up, "-12300", "50")]
    [InlineData("-12345", CashRoundingMode.Down, "-12400", "-55")]
    [InlineData("-12301", CashRoundingMode.Down, "-12400", "-99")]
    [InlineData("-12345", CashRoundingMode.Nearest, "-12300", "45")]
    [InlineData("-12350", CashRoundingMode.Nearest, "-12400", "-50")]
    [InlineData("-12351", CashRoundingMode.Nearest, "-12400", "-49")]
    public void RoundCash_negative_total_up_is_ceiling_down_is_floor(string total, CashRoundingMode mode, string rounded, string roundingAmount)
    {
        var result = Rupiah.RoundCash(D(total), 100m, mode);
        Assert.Equal(D(rounded), result.Total);
        Assert.Equal(D(roundingAmount), result.RoundingAmount);
    }

    [Theory]
    [InlineData("12345.5", 0, "12346")]       // rupiah default: no decimals
    [InlineData("12345.4999", 0, "12345")]
    [InlineData("-12345.5", 0, "-12346")]     // midpoint away from zero
    [InlineData("12.345", 2, "12.35")]
    [InlineData("12.3456", 4, "12.3456")]
    [InlineData("0", 0, "0")]
    public void RoundToCurrency_rounds_to_currency_decimals(string amount, int decimals, string expected)
    {
        Assert.Equal(D(expected), Rupiah.RoundToCurrency(D(amount), decimals));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void RoundToCurrency_rejects_decimals_outside_0_to_4(int decimals)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rupiah.RoundToCurrency(1m, decimals));
    }

    [Fact]
    public void Default_currency_decimals_is_zero()
    {
        Assert.Equal(0, Rupiah.DefaultCurrencyDecimals);
    }

    [Theory]
    [InlineData("up", CashRoundingMode.Up)]
    [InlineData("down", CashRoundingMode.Down)]
    [InlineData("nearest", CashRoundingMode.Nearest)]
    [InlineData("NEAREST", CashRoundingMode.Nearest)]
    public void ParseMode_reads_setting_value(string value, CashRoundingMode expected)
    {
        Assert.Equal(expected, CashRoundingModes.Parse(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ceil")]
    public void ParseMode_rejects_unknown_value(string value)
    {
        Assert.Throws<ArgumentException>(() => CashRoundingModes.Parse(value));
    }
}
