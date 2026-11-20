using System.Globalization;

namespace Pos.Tests;

internal static class TestDecimal
{
    /// <summary>Parses test data with invariant culture (the PC locale may use a decimal comma).</summary>
    public static decimal D(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
