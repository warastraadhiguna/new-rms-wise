namespace Pos.Shared.Sales;

/// <summary>Cash expected in a cashier's drawer at shift close.</summary>
public static class CashDrawer
{
    /// <summary>
    /// opening cash + cash handed over by customers - change given back.
    /// </summary>
    public static decimal ExpectedCash(decimal openingCash, decimal cashTendered, decimal changeGiven)
    {
        if (openingCash < 0)
            throw new ArgumentOutOfRangeException(nameof(openingCash), openingCash, "Opening cash must not be negative.");
        if (cashTendered < 0)
            throw new ArgumentOutOfRangeException(nameof(cashTendered), cashTendered, "Cash tendered must not be negative.");
        if (changeGiven < 0)
            throw new ArgumentOutOfRangeException(nameof(changeGiven), changeGiven, "Change must not be negative.");
        if (changeGiven > openingCash + cashTendered)
            throw new ArgumentOutOfRangeException(nameof(changeGiven), changeGiven, "Change exceeds the cash in the drawer.");

        return openingCash + cashTendered - changeGiven;
    }

    /// <summary>Positive = over, negative = short.</summary>
    public static decimal Difference(decimal countedCash, decimal expectedCash) => countedCash - expectedCash;
}
