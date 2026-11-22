using Pos.Shared.Money;

namespace Pos.Shared.Sales;

/// <summary>
/// Retur penjualan: the refund is the net price of the nota — after the line discount and the line's share of the
/// sale discount (Potongan/Diskon). Cash rounding of the nota is not refunded.
/// </summary>
public static class ReturnPricing
{
    /// <summary>Line total minus its share of the sale discount (share = line total / subtotal).</summary>
    public static decimal NetLineTotal(decimal lineTotal, decimal saleSubtotal, decimal saleDiscount)
    {
        if (saleSubtotal <= 0 || saleDiscount <= 0)
            return lineTotal;
        return lineTotal - saleDiscount * lineTotal / saleSubtotal;
    }

    /// <summary>"Harga retur" per selling unit, 4 decimals (stored on the return line).</summary>
    public static decimal NetUnitPrice(decimal netLineTotal, decimal soldQty) =>
        soldQty <= 0 ? 0m : Math.Round(netLineTotal / soldQty, 4, MidpointRounding.AwayFromZero);

    public static decimal Returnable(decimal soldQty, decimal returnedQty) => Math.Max(0m, soldQty - returnedQty);

    /// <summary>
    /// Refund for <paramref name="returnQty"/> units of a line of which <paramref name="returnedBefore"/> were returned
    /// earlier. Computed cumulatively (amount for all returned so far minus amount for the earlier ones, each rounded to
    /// the currency), so the partial refunds of a line always add up to its net total.
    /// </summary>
    /// <exception cref="SaleValidationException"><c>invalid_return_qty</c> or <c>exceeds_returnable</c>.</exception>
    public static decimal RefundAmount(decimal netLineTotal, decimal soldQty, decimal returnedBefore, decimal returnQty, int currencyDecimals)
    {
        if (returnQty <= 0)
            throw new SaleValidationException("invalid_return_qty", "The returned quantity must be greater than zero.");
        if (returnQty > Returnable(soldQty, returnedBefore))
            throw new SaleValidationException("exceeds_returnable",
                $"Only {Returnable(soldQty, returnedBefore)} of {soldQty} can still be returned.");

        decimal Cumulative(decimal qty) => Rupiah.RoundToCurrency(netLineTotal * qty / soldQty, currencyDecimals);
        return Cumulative(returnedBefore + returnQty) - Cumulative(returnedBefore);
    }

    /// <summary>Within <c>returns.window_days</c> branch days after the sale date (the last day included).</summary>
    public static bool WithinWindow(DateOnly saleDate, DateOnly today, int windowDays) =>
        today.DayNumber - saleDate.DayNumber <= Math.Max(0, windowDays);
}

/// <summary>How a return is settled (<c>sale_returns.settlement</c>).</summary>
public static class ReturnSettlements
{
    /// <summary>UANG KEMBALI: cash from the drawer of the open shift (reduces its expected cash).</summary>
    public const string Refund = "REFUND";

    /// <summary>TUKAR BARANG: the value pays (part of) a new sale (payment method RETURN).</summary>
    public const string Exchange = "EXCHANGE";

    /// <summary>POTONG PIUTANG: credit notas only; reduces what the customer still owes on that nota.</summary>
    public const string ReduceReceivable = "REDUCE_RECEIVABLE";

    public static IReadOnlyList<string> All { get; } = [Refund, Exchange, ReduceReceivable];

    public static string Label(string code) => code switch
    {
        Refund => "Uang Kembali",
        Exchange => "Tukar Barang",
        ReduceReceivable => "Potong Piutang",
        _ => code,
    };

    /// <exception cref="SaleValidationException"><c>invalid_settlement</c>, <c>not_a_credit_sale</c>, <c>exceeds_receivable</c>.</exception>
    public static void Validate(string settlement, decimal total, decimal saleCreditAmount, decimal outstanding)
    {
        if (!All.Contains(settlement))
            throw new SaleValidationException("invalid_settlement", $"Unknown settlement {settlement}.");
        if (settlement != ReduceReceivable)
            return;
        if (saleCreditAmount <= 0)
            throw new SaleValidationException("not_a_credit_sale", "Only a credit nota can be settled by reducing the receivable.");
        if (total > outstanding)
            throw new SaleValidationException("exceeds_receivable",
                $"The return ({total}) is more than the customer still owes on this nota ({outstanding}).");
    }
}
