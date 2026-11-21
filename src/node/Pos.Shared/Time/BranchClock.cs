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

    /// <summary>
    /// UTC moment the branch's <paramref name="date"/> begins (local midnight). A midnight skipped by daylight saving
    /// moves to the first valid local time of that day.
    /// </summary>
    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    /// <summary>
    /// Whole branch days <paramref name="from"/>..<paramref name="to"/> (inclusive) as a UTC range [start, end).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> is before <paramref name="from"/>.</exception>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) DayRangeUtc(DateOnly from, DateOnly to, TimeZoneInfo timeZone)
    {
        if (to < from)
            throw new ArgumentException($"The end date {to} is before the start date {from}.", nameof(to));
        return (StartOfDayUtc(from, timeZone), StartOfDayUtc(to.AddDays(1), timeZone));
    }
}
