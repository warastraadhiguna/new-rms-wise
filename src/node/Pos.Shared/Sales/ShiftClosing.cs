namespace Pos.Shared.Sales;

/// <summary>A payment method that does not go into the drawer (DEBIT, QRIS, TRANSFER, KREDIT) with its shift total.</summary>
/// <param name="Count">Number of sales paid (partly) with it.</param>
public sealed record ShiftMethodTotal(string Code, string Name, decimal Amount, int Count);

/// <summary>What happened in a cashier shift, as summed by the Node Service from its documents.</summary>
/// <param name="CashSales">Cash applied to sales without a credit part (tendered − change).</param>
/// <param name="CreditDownPayments">Cash paid as Uang Muka (DP) on credit sales.</param>
/// <param name="ReceivableCash">Credit sales paid off in cash during the shift.</param>
/// <param name="CashExpenses">Expenses paid from the drawer.</param>
/// <param name="NonCash">Non-cash methods (shown, never in the drawer).</param>
/// <param name="SaleCount">Notas (sales not voided).</param>
/// <param name="CancellationCount">Cancelled carts ("Batal") and voided sales.</param>
/// <param name="CashRefunds">Returns settled with cash from this drawer (UANG KEMBALI).</param>
public sealed record ShiftTotals(
    decimal OpeningCash,
    decimal CashSales,
    decimal CreditDownPayments,
    decimal ReceivableCash,
    decimal CashExpenses,
    IReadOnlyList<ShiftMethodTotal> NonCash,
    int SaleCount,
    int CancellationCount,
    decimal CashRefunds = 0m);

public enum ShiftDifferenceKind
{
    Exact,
    Over,
    Short,
}

/// <summary>Shift close: the cash that should be in the drawer and the difference with what was counted.</summary>
public static class ShiftClosing
{
    /// <summary>Modal awal + penjualan tunai + uang muka kredit + pelunasan piutang tunai − pengeluaran kas − retur tunai.</summary>
    public static decimal ExpectedCash(ShiftTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        NotNegative(totals.OpeningCash, nameof(totals.OpeningCash));
        NotNegative(totals.CashSales, nameof(totals.CashSales));
        NotNegative(totals.CreditDownPayments, nameof(totals.CreditDownPayments));
        NotNegative(totals.ReceivableCash, nameof(totals.ReceivableCash));
        NotNegative(totals.CashExpenses, nameof(totals.CashExpenses));
        NotNegative(totals.CashRefunds, nameof(totals.CashRefunds));
        NotNegative(totals.SaleCount, nameof(totals.SaleCount));
        NotNegative(totals.CancellationCount, nameof(totals.CancellationCount));

        return totals.OpeningCash + totals.CashSales + totals.CreditDownPayments + totals.ReceivableCash
               - totals.CashExpenses - totals.CashRefunds;
    }

    /// <summary>Positive = over (lebih), negative = short (kurang).</summary>
    public static decimal Difference(decimal countedCash, decimal expectedCash)
    {
        NotNegative(countedCash, nameof(countedCash));
        return countedCash - expectedCash;
    }

    public static ShiftDifferenceKind Kind(decimal difference) => difference switch
    {
        > 0 => ShiftDifferenceKind.Over,
        < 0 => ShiftDifferenceKind.Short,
        _ => ShiftDifferenceKind.Exact,
    };

    /// <summary>"Lebih", "Kurang" or "Pas".</summary>
    public static string Label(decimal difference) => Kind(difference) switch
    {
        ShiftDifferenceKind.Over => "Lebih",
        ShiftDifferenceKind.Short => "Kurang",
        _ => "Pas",
    };

    private static void NotNegative(decimal value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
    }

    private static void NotNegative(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
    }
}
