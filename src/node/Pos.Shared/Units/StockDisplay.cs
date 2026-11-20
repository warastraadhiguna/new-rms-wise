using System.Globalization;

namespace Pos.Shared.Units;

/// <summary>A base quantity split into whole larger units and the rest in the base unit (both non-negative).</summary>
public readonly record struct MixedQuantity(bool IsNegative, decimal Units, decimal Remainder);

/// <summary>
/// Stock as the cashier reads it. In the base unit (or any unit with conversion 1): a plain number.
/// In a larger unit: whole units plus the rest in the base unit, e.g. 38 PCS in BOX of 24 → "1 BOX 14 PCS",
/// -26 PCS → "-1 BOX 2 PCS". A base unit that allows decimals (KG) shows at most 2 decimals; whole-number
/// base units keep up to 4 so unexpected fractions stay visible.
/// </summary>
public static class StockDisplay
{
    public const int DecimalUnitDecimals = 2;

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    /// <summary>Splits |baseQty| (rounded for display first) into whole units of <paramref name="conversion"/> and the rest.</summary>
    public static MixedQuantity Split(decimal baseQty, decimal conversion, bool baseAllowsDecimal)
    {
        if (conversion <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversion), conversion, "Conversion must be greater than zero.");

        var rounded = RoundForDisplay(baseQty, baseAllowsDecimal);
        var abs = Math.Abs(rounded);
        var units = decimal.Floor(abs / conversion);
        var remainder = abs - units * conversion;
        return new MixedQuantity(rounded < 0, units, remainder);
    }

    /// <param name="baseQty">Stock in the product's base unit.</param>
    /// <param name="unitCode">The unit the row is shown in (e.g. BOX).</param>
    /// <param name="conversion">Base units per <paramref name="unitCode"/>.</param>
    /// <param name="baseUnitCode">The product's base unit (e.g. PCS).</param>
    /// <param name="baseAllowsDecimal"><c>units.allow_decimal</c> of the base unit.</param>
    /// <param name="culture">Number format; Indonesian ("1.234,5") when null.</param>
    public static string Format(
        decimal baseQty, string unitCode, decimal conversion, string baseUnitCode, bool baseAllowsDecimal,
        IFormatProvider? culture = null)
    {
        culture ??= Indonesian;
        var numberFormat = baseAllowsDecimal ? "#,0.##" : "#,0.####";

        if (conversion <= 0 || conversion == 1 || string.Equals(unitCode, baseUnitCode, StringComparison.OrdinalIgnoreCase))
        {
            var plain = RoundForDisplay(baseQty, baseAllowsDecimal);
            return (plain == 0 ? 0m : plain).ToString(numberFormat, culture);
        }

        var split = Split(baseQty, conversion, baseAllowsDecimal);
        var parts = new List<string>(2);
        if (split.Units > 0)
            parts.Add($"{split.Units.ToString("#,0", culture)} {unitCode}");
        if (split.Remainder > 0)
            parts.Add($"{split.Remainder.ToString(numberFormat, culture)} {baseUnitCode}");

        return parts.Count == 0 ? "0" : (split.IsNegative ? "-" : "") + string.Join(" ", parts);
    }

    private static decimal RoundForDisplay(decimal baseQty, bool baseAllowsDecimal) =>
        baseAllowsDecimal
            ? Math.Round(baseQty, DecimalUnitDecimals, MidpointRounding.AwayFromZero)
            : StorageScale.Round(baseQty);
}
