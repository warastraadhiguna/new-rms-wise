using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

public class MoneyTextFormatterTests
{
    [Theory]
    [InlineData("", 0, "", 0)]
    [InlineData("9", 1, "9", 1)]
    [InlineData("9000", 4, "9.000", 5)]
    [InlineData("90000", 5, "90.000", 6)]
    [InlineData("1234567", 7, "1.234.567", 9)]
    [InlineData("0", 1, "0", 1)]
    public void Groups_thousands_and_keeps_the_caret_after_the_same_digit(string text, int caret, string expected, int expectedCaret)
    {
        Assert.Equal((expected, expectedCaret), MoneyTextFormatter.Format(text, caret));
    }

    [Fact]
    public void Typing_in_the_middle_keeps_the_caret_after_the_typed_digit()
    {
        // "90.000" with "1" typed after "90." -> "90.1000" (caret 4) -> "901.000", caret after "901"
        Assert.Equal(("901.000", 3), MoneyTextFormatter.Format("90.1000", 4));
    }

    [Fact]
    public void Deleting_a_digit_regroups()
    {
        // "90.000" with the last 0 removed -> "90.00" (caret 5) -> "9.000"
        Assert.Equal(("9.000", 5), MoneyTextFormatter.Format("90.00", 5));
    }

    [Fact]
    public void Caret_at_the_start_stays_at_the_start()
    {
        Assert.Equal(("12.345", 0), MoneyTextFormatter.Format("12345", 0));
    }

    [Theory]
    [InlineData("1500,5", 6, "1.500,5", 7)]
    [InlineData("1500,", 5, "1.500,", 6)]
    [InlineData("12,5,0", 6, "12,50", 5)]          // only the first comma counts
    public void Decimal_comma_is_kept(string text, int caret, string expected, int expectedCaret)
    {
        Assert.Equal((expected, expectedCaret), MoneyTextFormatter.Format(text, caret));
    }

    [Theory]
    [InlineData("Rp 10000", 8, "10.000", 6)]
    [InlineData("-5000", 5, "5.000", 5)]
    [InlineData("12a34", 5, "1.234", 5)]
    [InlineData("00090", 5, "90", 2)]
    public void Other_characters_and_leading_zeros_are_dropped(string text, int caret, string expected, int expectedCaret)
    {
        Assert.Equal((expected, expectedCaret), MoneyTextFormatter.Format(text, caret));
    }

    [Theory]
    [InlineData("90.000", 3, 2)]   // just after the dot: Backspace removes the 0 before it, not the dot
    [InlineData("90.000", 4, 4)]
    [InlineData("90.000", 0, 0)]
    public void Backspace_skips_a_thousands_dot(string text, int caret, int expected)
    {
        Assert.Equal(expected, MoneyTextFormatter.CaretBeforeBackspace(text, caret));
    }

    [Theory]
    [InlineData("90.000", 2, 3)]   // just before the dot: Delete removes the 0 after it
    [InlineData("90.000", 1, 1)]
    [InlineData("90.000", 6, 6)]
    public void Delete_skips_a_thousands_dot(string text, int caret, int expected)
    {
        Assert.Equal(expected, MoneyTextFormatter.CaretBeforeDelete(text, caret));
    }

    [Theory]
    [InlineData("90.000", "90000")]
    [InlineData("1.500,50", "1500.50")]
    public void Formatted_text_still_parses(string text, string value)
    {
        Assert.True(MoneyInput.TryParse(text, out var parsed));
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), parsed);
    }
}
