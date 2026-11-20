using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>The money input behaviour on a real TextBox: type, Backspace, Delete, paste-like text.</summary>
[Collection(WpfUiCollection.Name)]
public class MoneyBoxUiTests
{
    private static void WithBox(Action<TextBox> test) => OnSta(() =>
    {
        var box = new TextBox();
        MoneyBox.SetIsEnabled(box, true);
        var window = Host(box);
        try
        {
            box.Focus();
            test(box);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Type(TextBox box, string digits)
    {
        foreach (var c in digits)
            TypeChar(box, c.ToString());
    }

    [Fact]
    public void Typing_90000_shows_90_000_with_the_caret_at_the_end() => WithBox(box =>
    {
        Type(box, "90000");

        Assert.Equal("90.000", box.Text);
        Assert.Equal(6, box.CaretIndex);
    });

    [Fact]
    public void Backspace_at_the_end_removes_one_digit() => WithBox(box =>
    {
        Type(box, "90000");

        PressKey(box, Key.Back);

        Assert.Equal("9.000", box.Text);
        Assert.Equal(5, box.CaretIndex);
    });

    [Fact]
    public void Backspace_right_after_the_dot_removes_the_digit_before_it() => WithBox(box =>
    {
        Type(box, "90000");
        box.CaretIndex = 3;   // "90.|000"

        PressKey(box, Key.Back);

        Assert.Equal("9.000", box.Text);
        Assert.Equal(1, box.CaretIndex);   // "9|.000"
    });

    [Fact]
    public void Delete_right_before_the_dot_removes_the_digit_after_it() => WithBox(box =>
    {
        Type(box, "90000");
        box.CaretIndex = 2;   // "90|.000"

        PressKey(box, Key.Delete);

        Assert.Equal("9.000", box.Text);
        Assert.Equal(3, box.CaretIndex);   // "9.0|00": still after the same two digits "9","0"
    });

    [Fact]
    public void Typing_in_the_middle_does_not_jump_the_caret() => WithBox(box =>
    {
        Type(box, "90000");
        box.CaretIndex = 3;   // "90.|000"

        TypeChar(box, "1");

        Assert.Equal("901.000", box.Text);
        Assert.Equal(3, box.CaretIndex);   // "901|.000"
    });

    [Fact]
    public void Letters_are_ignored_and_a_comma_starts_decimals() => WithBox(box =>
    {
        Type(box, "15a00,5");

        Assert.Equal("1.500,5", box.Text);
    });

    [Fact]
    public void Text_set_by_the_program_is_formatted_too() => WithBox(box =>
    {
        box.Text = "1234567";

        Assert.Equal("1.234.567", box.Text);
    });
}
