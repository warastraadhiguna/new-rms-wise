using System.Globalization;

namespace Pos.Tests;

internal static class TestDates
{
    /// <summary>Parses "yyyy-MM-dd" with invariant culture.</summary>
    public static DateOnly Day(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses a UTC timestamp like "2026-11-20T17:00:00Z".</summary>
    public static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
