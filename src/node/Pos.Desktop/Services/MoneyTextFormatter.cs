using System.Text;

namespace Pos.Desktop.Services;

/// <summary>
/// Formats money while it is typed, as in RMS 2: "90000" shows "90.000". Keeps the caret after the same
/// digit, keeps one decimal comma, drops anything else (letters, minus, "Rp", leading zeros).
/// </summary>
public static class MoneyTextFormatter
{
    public static (string Text, int Caret) Format(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        // significant characters: digits and the first comma; count those before the caret
        var integer = new StringBuilder();
        var fraction = new StringBuilder();
        var hasComma = false;
        var significantBeforeCaret = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var significant = char.IsAsciiDigit(c) || (c == ',' && !hasComma);
            if (!significant)
                continue;
            if (i < caret)
                significantBeforeCaret++;
            if (c == ',')
                hasComma = true;
            else if (hasComma)
                fraction.Append(c);
            else
                integer.Append(c);
        }

        // leading zeros: "00090" -> "90", but keep one "0"
        var digits = integer.ToString();
        var trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0 && digits.Length > 0)
            trimmed = "0";
        var removedZeros = digits.Length - trimmed.Length;
        significantBeforeCaret = Math.Max(0, significantBeforeCaret - Math.Min(removedZeros, significantBeforeCaret));

        var formatted = new StringBuilder(Group(trimmed));
        if (hasComma)
            formatted.Append(',').Append(fraction);
        var result = formatted.ToString();

        // caret: after the same number of significant characters
        var newCaret = 0;
        var seen = 0;
        while (newCaret < result.Length && seen < significantBeforeCaret)
        {
            if (result[newCaret] != '.')
                seen++;
            newCaret++;
        }
        return (result, newCaret);
    }

    /// <summary>Backspace right after a thousands dot should remove the digit before it, not the dot.</summary>
    public static int CaretBeforeBackspace(string text, int caret) =>
        caret > 0 && caret <= text.Length && text[caret - 1] == '.' ? caret - 1 : caret;

    /// <summary>Delete right before a thousands dot should remove the digit after it.</summary>
    public static int CaretBeforeDelete(string text, int caret) =>
        caret >= 0 && caret < text.Length && text[caret] == '.' ? caret + 1 : caret;

    private static string Group(string digits)
    {
        if (digits.Length <= 3)
            return digits;
        var sb = new StringBuilder();
        var head = digits.Length % 3;
        if (head > 0)
            sb.Append(digits, 0, head);
        for (var i = head; i < digits.Length; i += 3)
        {
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(digits, i, 3);
        }
        return sb.ToString();
    }
}
