namespace Pos.Shared.Units;

/// <summary>
/// Converts between a product unit (e.g. BOX) and the product's base unit (e.g. PCS).
/// <c>conversion</c> is <c>product_units.conversion</c>: how many base units one unit holds (1 BOX = 24 PCS -> 24).
/// <c>allowDecimal</c> is <c>units.allow_decimal</c> of the unit the quantity is expressed in.
/// </summary>
public static class UnitConversion
{
    /// <summary>qty (in the given unit) x conversion -> base_qty.</summary>
    /// <exception cref="InvalidQuantityException">qty is fractional for a whole-number unit, or has more than 4 decimals.</exception>
    public static decimal ToBaseQty(decimal qty, decimal conversion, bool allowDecimal)
    {
        EnsureValidConversion(conversion);

        if (!StorageScale.Fits(qty))
            throw new InvalidQuantityException($"Quantity {qty} has more than {StorageScale.Decimals} decimals.", nameof(qty));

        if (!allowDecimal && !IsWhole(qty))
            throw new InvalidQuantityException($"Quantity {qty} must be a whole number for this unit.", nameof(qty));

        return StorageScale.Round(qty * conversion);
    }

    /// <summary>base_qty / conversion -> qty in the given unit.</summary>
    /// <exception cref="InvalidQuantityException">The result is not a whole number and the unit does not allow decimals.</exception>
    public static decimal FromBaseQty(decimal baseQty, decimal conversion, bool allowDecimal)
    {
        EnsureValidConversion(conversion);

        var qty = baseQty / conversion;

        if (!allowDecimal && !IsWhole(qty))
            throw new InvalidQuantityException(
                $"Base quantity {baseQty} is not a whole number of units (conversion {conversion}).", nameof(baseQty));

        return StorageScale.Round(qty);
    }

    private static bool IsWhole(decimal value) => decimal.Truncate(value) == value;

    private static void EnsureValidConversion(decimal conversion)
    {
        if (conversion <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversion), conversion, "Conversion must be greater than zero.");
    }
}
