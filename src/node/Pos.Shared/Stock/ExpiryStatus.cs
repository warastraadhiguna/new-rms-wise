namespace Pos.Shared.Stock;

public enum ExpiryState
{
    NoExpiry,
    Ok,
    NearExpiry,
    Expired,
}

/// <summary>
/// "Form Cek Stok" markers. Expired = expiry date before today (branch date), the same rule FEFO uses to skip
/// stock; near = expiring within <c>stock.expiry_warning_days</c> (default 30), today included.
/// </summary>
public static class ExpiryStatus
{
    public const int DefaultWarningDays = 30;
    public const string WarningDaysSetting = "stock.expiry_warning_days";

    public static ExpiryState Evaluate(DateOnly? expiry, DateOnly today, int warningDays)
    {
        if (expiry is not { } date)
            return ExpiryState.NoExpiry;
        if (date < today)
            return ExpiryState.Expired;
        return date <= today.AddDays(Math.Max(0, warningDays)) ? ExpiryState.NearExpiry : ExpiryState.Ok;
    }

    /// <summary>Text shown next to the stock, as in RMS 2.</summary>
    public static string? Label(ExpiryState state) => state switch
    {
        ExpiryState.NearExpiry => "(HAMPIR EXPIRED)",
        ExpiryState.Expired => "(SUDAH EXPIRED)",
        _ => null,
    };

    /// <summary>API value (snake_case).</summary>
    public static string ToCode(ExpiryState state) => state switch
    {
        ExpiryState.NoExpiry => "no_expiry",
        ExpiryState.Ok => "ok",
        ExpiryState.NearExpiry => "near_expiry",
        ExpiryState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static ExpiryState FromCode(string? code) => code switch
    {
        "ok" => ExpiryState.Ok,
        "near_expiry" => ExpiryState.NearExpiry,
        "expired" => ExpiryState.Expired,
        _ => ExpiryState.NoExpiry,
    };
}
