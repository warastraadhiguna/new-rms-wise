using System.Globalization;
using Pos.Shared.Sales;

namespace Pos.Shared.Printing;

/// <param name="UnitPrice">Net refund price per unit (after line and sale discount).</param>
/// <param name="Damaged">Kondisi RUSAK (went to the DAMAGED location).</param>
public sealed record ReturnReceiptItem(string Name, decimal Qty, string UnitCode, decimal UnitPrice, decimal LineTotal, bool Damaged);

/// <summary>Everything printed on a "Bukti Retur Penjualan". Times are already in the branch timezone.</summary>
public sealed record ReturnReceiptData(
    IReadOnlyList<string> HeaderLines,
    string Number,
    DateTime ReturnedAtLocal,
    string SaleNumber,
    string CashierName,
    string? CustomerName,
    IReadOnlyList<ReturnReceiptItem> Items,
    decimal Total,
    string Settlement,
    string? ReasonName,
    string? Notes);

/// <summary>
/// "Bukti Retur Penjualan" on the receipt printer: store header, return and nota numbers, items (qty x net price,
/// Kondisi RUSAK when damaged), Total Retur, Penyelesaian (Uang Kembali / Tukar Barang / Potong Piutang), Alasan,
/// signature places for the customer and the cashier.
/// </summary>
public static class ReturnReceiptFormatter
{
    private const int FieldLabelWidth = 9;

    public static IReadOnlyList<string> Format(ReturnReceiptData data, int width = ReceiptFormatter.Width80mm, bool reprint = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (width < ReceiptFormatter.MinWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"A receipt needs at least {ReceiptFormatter.MinWidth} characters per line.");

        var rule = new string('-', width);
        var lines = new List<string>();
        foreach (var header in data.HeaderLines)
            lines.AddRange(ReceiptFormatter.Centered(header, width));
        lines.Add(rule);
        lines.AddRange(ReceiptFormatter.Centered("BUKTI RETUR PENJUALAN", width));
        if (reprint)
            lines.AddRange(ReceiptFormatter.Centered("CETAK ULANG", width));
        lines.Add(rule);

        lines.Add(Field("No Retur", data.Number, width));
        lines.Add(Field("Tanggal", data.ReturnedAtLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), width));
        lines.Add(Field("No Nota", data.SaleNumber, width));
        lines.Add(Field("Kasir", data.CashierName, width));
        if (!string.IsNullOrWhiteSpace(data.CustomerName))
            lines.Add(Field("Member", data.CustomerName, width));
        lines.Add(rule);

        foreach (var item in data.Items)
        {
            lines.AddRange(ReceiptFormatter.Wrap(item.Name, width).Take(2));
            var detail = $"  {ReceiptFormatter.Number(item.Qty)} {item.UnitCode} x {ReceiptFormatter.Number(item.UnitPrice)}";
            var total = ReceiptFormatter.Number(item.LineTotal);
            if (detail.Length + 1 + total.Length <= width)
                lines.Add(detail + total.PadLeft(width - detail.Length));
            else
            {
                lines.Add(ReceiptFormatter.Fit(detail, width));
                lines.Add(total.PadLeft(width));
            }
            if (item.Damaged)
                lines.Add("  Kondisi: RUSAK");
        }
        lines.Add(rule);

        lines.AddRange(ReceiptFormatter.Row("Total Retur", ReceiptFormatter.Number(data.Total), width));
        lines.AddRange(ReceiptFormatter.Row("Penyelesaian", ReturnSettlements.Label(data.Settlement), width));
        var reason = string.Join(" - ", new[] { data.ReasonName, data.Notes }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (reason.Length > 0)
            lines.AddRange(ReceiptFormatter.Wrap($"Alasan: {reason}", width));
        lines.Add(rule);

        var column = width / 2;
        string Pair(string left, string right) => (Center(left, column) + Center(right, width - column)).TrimEnd();
        lines.Add("");
        lines.Add(Pair("Pelanggan,", "Kasir,"));
        lines.AddRange(["", "", ""]);
        var sign = "(" + new string('_', Math.Max(4, column - 4)) + ")";
        lines.Add(Pair(sign, sign));
        return lines;
    }

    private static string Center(string text, int width)
    {
        text = ReceiptFormatter.Fit(text, width);
        var left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }

    private static string Field(string label, string value, int width) =>
        ReceiptFormatter.Fit($"{label.PadRight(FieldLabelWidth)}: {value}", width);
}
