using Pos.Shared.Money;

namespace Pos.Shared.Sales;

/// <summary>
/// Invariants: all three at currency precision, <c>LineTotal == Gross - DiscountAmount</c>,
/// <c>0 &lt;= DiscountAmount &lt;= Gross</c>, <c>LineTotal &gt;= 0</c>.
/// </summary>
/// <param name="Gross">qty x unit_price.</param>
/// <param name="DiscountAmount">Total line discount (<c>sale_lines.discount_amount</c>).</param>
/// <param name="LineTotal">Gross - DiscountAmount (<c>sale_lines.line_total</c>).</param>
public readonly record struct SaleLineAmounts(decimal Gross, decimal DiscountAmount, decimal LineTotal);

/// <summary>
/// Line amounts before tax. Percent discount is applied to the gross first,
/// then the nominal discount (per line, not per unit) is subtracted.
/// Gross is rounded to the currency precision before discounts; unit price keeps 4 dp.
/// </summary>
public static class SaleLineCalculator
{
    public static SaleLineAmounts Calculate(
        decimal qty,
        decimal unitPrice,
        decimal discountPct = 0m,
        decimal discountAmount = 0m,
        int currencyDecimals = Rupiah.DefaultCurrencyDecimals)
    {
        Rupiah.EnsureValidCurrencyDecimals(currencyDecimals);
        if (qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), qty, "Quantity must be greater than zero.");
        if (unitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), unitPrice, "Unit price must not be negative.");
        if (discountPct is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(discountPct), discountPct, "Discount percent must be between 0 and 100.");
        if (discountAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(discountAmount), discountAmount, "Discount amount must not be negative.");

        var gross = Rupiah.RoundToCurrency(qty * unitPrice, currencyDecimals);
        var pctDiscount = gross * discountPct / 100m;
        // Gross is already on the currency grid, so rounding a value <= gross cannot exceed it.
        var discount = Rupiah.RoundToCurrency(Math.Min(gross, pctDiscount + discountAmount), currencyDecimals);

        return new SaleLineAmounts(gross, discount, gross - discount);
    }
}
