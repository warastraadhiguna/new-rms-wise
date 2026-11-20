using System.Globalization;
using System.Text;

namespace Pos.Shared.Printing;

public static class ReceiptDefaults
{
    /// <summary>Footer when the setting <c>receipt.footer_lines</c> is missing (RMS 2 text).</summary>
    public const string FooterLine = "Terimakasih Atas Kunjungannya";
}

/// <param name="Name">Product name; printed on up to two lines.</param>
/// <param name="LineTotal">Subtotal of the line (after line discounts).</param>
public sealed record ReceiptItem(string Name, decimal Qty, string UnitCode, decimal UnitPrice, decimal LineTotal);

/// <param name="Amount">Applied to the sale.</param>
/// <param name="Tendered">Cash handed over (null for non-cash).</param>
public sealed record ReceiptPayment(string MethodName, bool IsCash, bool IsCredit, decimal Amount, decimal? Tendered, decimal? Change);

/// <summary>Everything printed on a sale receipt. Times are already in the branch timezone.</summary>
/// <param name="DiscountPct">Set when the sale discount was given as a percentage ("Diskon 10%"), otherwise "Potongan".</param>
/// <param name="DueDate">Credit sales: when the receivable is due.</param>
public sealed record ReceiptData(
    IReadOnlyList<string> HeaderLines,
    IReadOnlyList<string> FooterLines,
    string Number,
    DateTime SoldAtLocal,
    string CashierName,
    string? CustomerName,
    IReadOnlyList<ReceiptItem> Items,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal? DiscountPct,
    decimal RoundingAmount,
    decimal Total,
    IReadOnlyList<ReceiptPayment> Payments,
    DateOnly? DueDate);

/// <summary>
/// Lays out an RMS 2 style receipt as plain text lines for a fixed-width thermal printer:
/// header, (CETAK ULANG), number/date/cashier, items ("qty unit x price" + subtotal on the right),
/// SubTotal, Potongan/Diskon, Pembulatan (when not zero), Grand Total, Cara Bayar, Bayar, Kembali, footer.
/// Numbers use dot thousands and a decimal comma. No line is wider than the paper.
/// </summary>
public static class ReceiptFormatter
{
    /// <summary>Font A characters per line on 80 mm paper (e.g. EPSON TM-T82II).</summary>
    public const int Width80mm = 48;

    public const int Width58mm = 32;

    public const int MinWidth = 24;

    private const int MaxNameLines = 2;
    private const int FieldLabelWidth = 7;

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public static IReadOnlyList<string> Format(ReceiptData data, int width = Width80mm, bool reprint = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (width < MinWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"A receipt needs at least {MinWidth} characters per line.");

        var rule = new string('-', width);
        var lines = new List<string>();

        foreach (var header in data.HeaderLines)
            lines.AddRange(Centered(header, width));
        if (reprint)
            lines.AddRange(Centered("CETAK ULANG", width));

        lines.Add(rule);
        lines.Add(Field("No", data.Number, width));
        lines.Add(Field("Tanggal", data.SoldAtLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), width));
        lines.Add(Field("Kasir", data.CashierName, width));
        if (!string.IsNullOrWhiteSpace(data.CustomerName))
            lines.Add(Field("Member", data.CustomerName, width));
        lines.Add(rule);

        foreach (var item in data.Items)
        {
            lines.AddRange(Wrap(item.Name, width).Take(MaxNameLines));
            lines.AddRange(ItemDetail(item, width));
        }
        lines.Add(rule);

        lines.AddRange(Row("SubTotal", Number(data.Subtotal), width));
        if (data.DiscountAmount != 0)
        {
            var label = data.DiscountPct is { } pct and not 0 ? $"Diskon {Number(pct)}%" : "Potongan";
            lines.AddRange(Row(label, Number(data.DiscountAmount), width));
        }
        if (data.RoundingAmount != 0)
            lines.AddRange(Row("Pembulatan", Number(data.RoundingAmount), width));
        lines.AddRange(Row("Grand Total", Number(data.Total), width));

        var methods = data.Payments.Count == 0 ? "-" : string.Join(" + ", data.Payments.Select(p => p.MethodName));
        lines.AddRange(Row("Cara Bayar", methods, width));
        lines.AddRange(Row("Bayar", Number(data.Payments.Sum(p => p.Tendered ?? p.Amount)), width));
        lines.AddRange(Row("Kembali", Number(data.Payments.Sum(p => p.Change ?? 0m)), width));
        if (data.DueDate is { } due)
            lines.AddRange(Row("Jatuh Tempo", due.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), width));
        lines.Add(rule);

        foreach (var footer in data.FooterLines)
            lines.AddRange(Centered(footer, width));

        return lines;
    }

    /// <summary>Indonesian number: "1.250.000", "0,5", "-50".</summary>
    public static string Number(decimal value) => value.ToString("#,0.####", Indonesian);

    /// <summary>"  3 PCS x 4.000" with the subtotal on the right, or on its own line when it does not fit.</summary>
    private static IEnumerable<string> ItemDetail(ReceiptItem item, int width)
    {
        const string indent = "  ";
        var detail = Wrap($"{Number(item.Qty)} {item.UnitCode} x {Number(item.UnitPrice)}", width - indent.Length)
            .Select(l => indent + l).ToList();
        var total = Fit(Number(item.LineTotal), width);

        if (detail[^1].Length + 1 + total.Length <= width)
            detail[^1] = detail[^1] + total.PadLeft(width - detail[^1].Length);
        else
            detail.Add(total.PadLeft(width));
        return detail;
    }

    /// <summary>Label on the left, value on the right; the value moves to its own line when both do not fit.</summary>
    private static IEnumerable<string> Row(string label, string value, int width)
    {
        if (label.Length + 1 + value.Length <= width)
            return [label + value.PadLeft(width - label.Length)];
        return [Fit(label, width), Fit(value, width).PadLeft(width)];
    }

    /// <summary>"Kasir  : Kasir Satu", cut at the paper width.</summary>
    private static string Field(string label, string value, int width) =>
        Fit($"{label.PadRight(FieldLabelWidth)}: {value}", width);

    private static IEnumerable<string> Centered(string text, int width) =>
        Wrap(text, width).Select(l => new string(' ', (width - l.Length) / 2) + l);

    /// <summary>Greedy word wrap; words longer than the line are cut. Blank text gives one empty line.</summary>
    internal static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (rest.Length > 0)
            {
                var needed = current.Length == 0 ? rest.Length : current.Length + 1 + rest.Length;
                if (needed <= width)
                {
                    if (current.Length > 0)
                        current.Append(' ');
                    current.Append(rest);
                    rest = "";
                }
                else if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    lines.Add(rest[..width]);
                    rest = rest[width..];
                }
            }
        }
        if (current.Length > 0 || lines.Count == 0)
            lines.Add(current.ToString());
        return lines;
    }

    private static string Fit(string text, int width) => text.Length <= width ? text : text[..width];
}
