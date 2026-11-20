namespace Pos.Shared.Money;

/// <summary>Result of cash rounding. <c>Total = original + RoundingAmount</c>.</summary>
/// <param name="Total">Amount the customer pays (or receives).</param>
/// <param name="RoundingAmount">Signed difference; stored in <c>sales.rounding_amount</c>.</param>
public readonly record struct CashRoundingResult(decimal Total, decimal RoundingAmount);

/// <summary>
/// The single place for rupiah rounding rules.
/// </summary>
public static class Rupiah
{
    /// <summary>Default for setting <c>currency.decimals</c>: rupiah has no fractional part.</summary>
    public const int DefaultCurrencyDecimals = 0;

    /// <summary>Rounds an amount to storage precision (4 decimals, midpoint away from zero).</summary>
    public static decimal Round(decimal amount) => StorageScale.Round(amount);

    /// <summary>
    /// Rounds an amount to the currency precision (setting <c>currency.decimals</c>, 0..4), midpoint away from zero.
    /// </summary>
    public static decimal RoundToCurrency(decimal amount, int currencyDecimals)
    {
        EnsureValidCurrencyDecimals(currencyDecimals);
        return decimal.Round(amount, currencyDecimals, MidpointRounding.AwayFromZero);
    }

    public static void EnsureValidCurrencyDecimals(int currencyDecimals)
    {
        if (currencyDecimals is < 0 or > StorageScale.Decimals)
            throw new ArgumentOutOfRangeException(nameof(currencyDecimals), currencyDecimals,
                $"Currency decimals must be between 0 and {StorageScale.Decimals}.");
    }

    /// <summary>
    /// Rounds a cash total to a multiple of <paramref name="step"/> (setting <c>rounding.cash_step</c>, e.g. 100 or 500).
    /// A step of 0 disables rounding. Up = toward +infinity, Down = toward -infinity (also for negative totals),
    /// so a mode always favours the same party. Nearest: exact half step goes away from zero.
    /// </summary>
    public static CashRoundingResult RoundCash(decimal total, decimal step, CashRoundingMode mode)
    {
        if (step < 0)
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step must not be negative.");

        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cash rounding mode.");

        if (step == 0)
            return new CashRoundingResult(total, 0m);

        var units = total / step;
        var roundedUnits = mode switch
        {
            CashRoundingMode.Up => decimal.Ceiling(units),
            CashRoundingMode.Down => decimal.Floor(units),
            _ => decimal.Round(units, 0, MidpointRounding.AwayFromZero),
        };

        var rounded = roundedUnits * step;
        return new CashRoundingResult(rounded, rounded - total);
    }
}
