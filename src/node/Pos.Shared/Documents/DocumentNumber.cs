using System.Globalization;
using System.Text.RegularExpressions;

namespace Pos.Shared.Documents;

/// <summary>Type codes used in document numbers and <c>document_sequences.doc_type</c>.</summary>
public static class DocumentTypes
{
    public const string Sale = "SL";
    public const string CashierShift = "SH";
    public const string StockAdjustment = "ADJ";

    public static IReadOnlyList<string> All { get; } = [Sale, CashierShift, StockAdjustment];
}

/// <summary>
/// User-facing document numbers: <c>{NODE}-{TYPE}-{YYYYMM}-{000001}</c>, e.g. <c>TK01-SL-202611-000123</c>.
/// The running number restarts every month (branch-local month) per document type.
/// </summary>
public static partial class DocumentNumber
{
    public static string Format(string nodeCode, string typeCode, string period, int number)
    {
        if (string.IsNullOrEmpty(nodeCode) || !CodePattern().IsMatch(nodeCode))
            throw new ArgumentException($"Invalid node code '{nodeCode}'.", nameof(nodeCode));
        if (string.IsNullOrEmpty(typeCode) || !CodePattern().IsMatch(typeCode))
            throw new ArgumentException($"Invalid document type code '{typeCode}'.", nameof(typeCode));
        if (!IsValidPeriod(period))
            throw new ArgumentException($"Invalid period '{period}', expected YYYYMM.", nameof(period));
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), number, "Number must be greater than zero.");

        return $"{nodeCode}-{typeCode}-{period}-{number.ToString("D6", CultureInfo.InvariantCulture)}";
    }

    /// <summary>YYYYMM of <paramref name="utc"/> in the branch timezone (IANA id).</summary>
    public static string Period(DateTime utc, string timeZoneId) =>
        Period(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

    public static string Period(DateTime utc, TimeZoneInfo timeZone)
    {
        if (utc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(utc));

        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone).ToString("yyyyMM", CultureInfo.InvariantCulture);
    }

    private static bool IsValidPeriod(string period) =>
        period is { Length: 6 }
        && period.All(char.IsAsciiDigit)
        && int.Parse(period.AsSpan(4, 2), CultureInfo.InvariantCulture) is >= 1 and <= 12;

    [GeneratedRegex("^[A-Z0-9]+$")]
    private static partial Regex CodePattern();
}
