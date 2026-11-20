namespace Pos.Shared;

/// <summary>
/// Precision of every money and quantity column: DECIMAL(18,4).
/// </summary>
public static class StorageScale
{
    public const int Decimals = 4;

    /// <summary>Rounds to 4 decimals, midpoint away from zero.</summary>
    public static decimal Round(decimal value) =>
        decimal.Round(value, Decimals, MidpointRounding.AwayFromZero);

    /// <summary>True when the value has no digits beyond 4 decimals.</summary>
    public static bool Fits(decimal value) => Round(value) == value;
}
