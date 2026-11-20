namespace Pos.Shared.Time;

/// <summary>
/// Converts Node Service UTC time to the branch's calendar (<c>branches.timezone</c>, IANA id such as "Asia/Jakarta").
/// </summary>
public static class BranchClock
{
    /// <summary>The branch's local date at <paramref name="utcNow"/>.</summary>
    /// <exception cref="TimeZoneNotFoundException">Unknown timezone id.</exception>
    public static DateOnly Today(DateTime utcNow, string timeZoneId) =>
        Today(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

    public static DateOnly Today(DateTime utcNow, TimeZoneInfo timeZone)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(utcNow));

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));
    }
}
