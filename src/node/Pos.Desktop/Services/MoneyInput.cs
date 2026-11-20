using System.Globalization;

namespace Pos.Desktop.Services;

/// <summary>Reads amounts typed by Indonesian users: "200.000", "1.500,50", "Rp 75.000". Never negative.</summary>
public static class MoneyInput
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        var cleaned = (text ?? "").Trim();
        if (cleaned.StartsWith("Rp", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..].Trim();
        if (cleaned.Length == 0)
            return false;

        return decimal.TryParse(cleaned, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, Indonesian, out value)
            && value >= 0;
    }
}
