namespace Pos.Shared.Money;

/// <summary>Direction for cash rounding. Signed, so a mode always favours the same party.</summary>
public enum CashRoundingMode
{
    /// <summary>Toward +infinity (ceiling) to a multiple of the step.</summary>
    Up,

    /// <summary>Toward -infinity (floor) to a multiple of the step.</summary>
    Down,

    /// <summary>Closest multiple; exactly half a step goes up (away from zero).</summary>
    Nearest,
}

/// <summary>String values used in settings (e.g. <c>rounding.cash_mode</c>).</summary>
public static class CashRoundingModes
{
    public const string Up = "up";
    public const string Down = "down";
    public const string Nearest = "nearest";

    public static CashRoundingMode Parse(string value) => value?.Trim().ToLowerInvariant() switch
    {
        Up => CashRoundingMode.Up,
        Down => CashRoundingMode.Down,
        Nearest => CashRoundingMode.Nearest,
        _ => throw new ArgumentException($"Unknown cash rounding mode '{value}'.", nameof(value)),
    };

    public static string ToCode(this CashRoundingMode mode) => mode switch
    {
        CashRoundingMode.Up => Up,
        CashRoundingMode.Down => Down,
        CashRoundingMode.Nearest => Nearest,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
