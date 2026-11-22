namespace Pos.Shared.Sales;

/// <summary>Status of a sale in Data Penjualan (values are stable codes; <see cref="Label"/> is the Indonesian text).</summary>
public static class SaleStatuses
{
    public const string Paid = "PAID";
    public const string Unpaid = "UNPAID";
    public const string Returned = "RETURNED";
    public const string Void = "VOID";

    /// <summary>Lunas, Belum Lunas, Ada Retur, Void.</summary>
    public static string Label(string code) => code switch
    {
        Paid => "Lunas",
        Unpaid => "Belum Lunas",
        Returned => "Ada Retur",
        Void => "Void",
        _ => code,
    };
}

/// <summary>What a customer still owes on a sale ("Total Tertanggung") and the sale's status.</summary>
public static class SaleBalance
{
    /// <summary>
    /// Credit part − receivable payments allocated to the sale − returns settled by reducing the receivable;
    /// never negative; zero when the sale is voided (its receivable is cancelled).
    /// </summary>
    public static decimal Outstanding(decimal creditAmount, decimal paidAllocations, decimal reducedByReturns, bool voided)
    {
        if (creditAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(creditAmount), creditAmount, "Must not be negative.");
        if (paidAllocations < 0)
            throw new ArgumentOutOfRangeException(nameof(paidAllocations), paidAllocations, "Must not be negative.");
        if (reducedByReturns < 0)
            throw new ArgumentOutOfRangeException(nameof(reducedByReturns), reducedByReturns, "Must not be negative.");
        return voided ? 0m : Math.Max(0m, creditAmount - paidAllocations - reducedByReturns);
    }

    /// <summary>Void first; then Belum Lunas while money is owed; then Ada Retur; otherwise Lunas.</summary>
    public static string Status(bool voided, decimal outstanding, bool hasReturn) =>
        voided ? SaleStatuses.Void
        : outstanding > 0 ? SaleStatuses.Unpaid
        : hasReturn ? SaleStatuses.Returned
        : SaleStatuses.Paid;
}
