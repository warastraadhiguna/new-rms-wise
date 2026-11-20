using System.Globalization;

namespace Pos.Shared.Time;

/// <summary>A job that runs once a day at a local time of the branch (e.g. the nightly stock integrity check).</summary>
public static class DailySchedule
{
    /// <summary>
    /// The first moment strictly after <paramref name="utcNow"/> when the branch clock shows <paramref name="at"/>.
    /// A local time skipped by daylight saving runs one hour later that day.
    /// </summary>
    public static DateTime NextUtc(DateTime utcNow, TimeOnly at, TimeZoneInfo timeZone)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(utcNow));
        ArgumentNullException.ThrowIfNull(timeZone);

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));
        for (var day = localToday.AddDays(-1); ; day = day.AddDays(1))
        {
            var candidate = ToUtc(day.ToDateTime(at, DateTimeKind.Unspecified), timeZone);
            if (candidate > utcNow)
                return candidate;
        }
    }

    /// <summary>"HH:mm" (24 hours), as written in configuration.</summary>
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }
}
