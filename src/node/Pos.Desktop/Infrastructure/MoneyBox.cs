using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.Infrastructure;

/// <summary>
/// One behaviour for every money input (<c>infra:MoneyBox.IsEnabled="True"</c>): thousands dots while
/// typing ("90000" → "90.000"), caret stays after the same digit, Backspace/Delete skip the dots,
/// only digits and one decimal comma can be typed. The bound string still parses with MoneyInput.
/// </summary>
public static class MoneyBox
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(MoneyBox), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty IsFormattingProperty =
        DependencyProperty.RegisterAttached("IsFormatting", typeof(bool), typeof(MoneyBox));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.TextChanged -= OnTextChanged;
        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is not true)
            return;

        box.TextChanged += OnTextChanged;
        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        Reformat(box);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        foreach (var c in e.Text)
        {
            var allowed = char.IsAsciiDigit(c) || (c == ',' && !box.Text.Contains(','));
            if (!allowed)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.SelectionLength > 0)
            return;

        if (e.Key == Key.Back)
            box.CaretIndex = MoneyTextFormatter.CaretBeforeBackspace(box.Text, box.CaretIndex);
        else if (e.Key == Key.Delete)
            box.CaretIndex = MoneyTextFormatter.CaretBeforeDelete(box.Text, box.CaretIndex);
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e) => Reformat((TextBox)sender);

    private static void Reformat(TextBox box)
    {
        if ((bool)box.GetValue(IsFormattingProperty))
            return;

        var (text, caret) = MoneyTextFormatter.Format(box.Text, box.CaretIndex);
        if (text == box.Text)
            return;

        box.SetValue(IsFormattingProperty, true);
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            box.SetValue(IsFormattingProperty, false);
        }
    }
}
