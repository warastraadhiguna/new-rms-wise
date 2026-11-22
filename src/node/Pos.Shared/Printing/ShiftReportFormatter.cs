using System.Globalization;
using Pos.Shared.Sales;

namespace Pos.Shared.Printing;

/// <summary>Everything printed on the shift close report. Times are already in the branch timezone.</summary>
/// <param name="ClosedByName">Set when someone other than the cashier closed the shift.</param>
public sealed record ShiftReportData(
    IReadOnlyList<string> HeaderLines,
    string ShiftNumber,
    string CashierName,
    DateTime OpenedAtLocal,
    DateTime ClosedAtLocal,
    string? ClosedByName,
    string? Notes,
    ShiftTotals Totals,
    decimal ExpectedCash,
    decimal CountedCash);

/// <summary>
/// "Laporan Tutup Shift" on the receipt printer: store header, shift and cashier, the cash figures (Modal Awal,
/// Penjualan Tunai, Uang Muka Kredit, Pelunasan Piutang Tunai, Pengeluaran Kas, Kas Seharusnya, Kas Fisik,
/// Selisih Lebih/Kurang/Pas), non-cash methods with their nota count, Jumlah Nota, Jumlah Pembatalan, then signature
/// places for the cashier and the supervisor.
/// </summary>
public static class ShiftReportFormatter
{
    private const int FieldLabelWidth = 9;
    private const int SignatureBlankLines = 3;

    public static IReadOnlyList<string> Format(ShiftReportData data, int width = ReceiptFormatter.Width80mm, bool reprint = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (width < ReceiptFormatter.MinWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"A receipt needs at least {ReceiptFormatter.MinWidth} characters per line.");

        var rule = new string('-', width);
        var lines = new List<string>();
        foreach (var header in data.HeaderLines)
            lines.AddRange(ReceiptFormatter.Centered(header, width));
        lines.Add(rule);
        lines.AddRange(ReceiptFormatter.Centered("LAPORAN TUTUP SHIFT", width));
        if (reprint)
            lines.AddRange(ReceiptFormatter.Centered("CETAK ULANG", width));
        lines.Add(rule);

        lines.Add(Field("No Shift", data.ShiftNumber, width));
        lines.Add(Field("Kasir", data.CashierName, width));
        lines.Add(Field("Dibuka", Time(data.OpenedAtLocal), width));
        lines.Add(Field("Ditutup", Time(data.ClosedAtLocal), width));
        if (!string.IsNullOrWhiteSpace(data.ClosedByName))
            lines.Add(ReceiptFormatter.Fit($"Ditutup oleh: {data.ClosedByName}", width));
        if (!string.IsNullOrWhiteSpace(data.Notes))
            lines.AddRange(ReceiptFormatter.Wrap($"Keterangan: {data.Notes.Trim()}", width));
        lines.Add(rule);

        var t = data.Totals;
        var difference = ShiftClosing.Difference(data.CountedCash, data.ExpectedCash);
        lines.AddRange(Row("Modal Awal", t.OpeningCash, width));
        lines.AddRange(Row("Penjualan Tunai", t.CashSales, width));
        lines.AddRange(Row("Uang Muka Kredit", t.CreditDownPayments, width));
        lines.AddRange(Row("Pelunasan Piutang Tunai", t.ReceivableCash, width));
        lines.AddRange(Row("Pengeluaran Kas", t.CashExpenses == 0 ? 0m : -t.CashExpenses, width));
        lines.AddRange(Row("Retur Tunai", t.CashRefunds == 0 ? 0m : -t.CashRefunds, width));
        lines.Add(rule);
        lines.AddRange(Row("Kas Seharusnya", data.ExpectedCash, width));
        lines.AddRange(Row("Kas Fisik", data.CountedCash, width));
        lines.AddRange(Row($"Selisih ({ShiftClosing.Label(difference)})", difference, width));
        lines.Add(rule);

        if (t.NonCash.Count > 0)
        {
            lines.Add("Non Tunai (jumlah nota)");
            foreach (var method in t.NonCash)
                lines.AddRange(Row($"{method.Name} ({method.Count})", method.Amount, width));
            lines.Add(rule);
        }
        lines.AddRange(ReceiptFormatter.Row("Jumlah Nota", t.SaleCount.ToString(CultureInfo.InvariantCulture), width));
        lines.AddRange(ReceiptFormatter.Row("Jumlah Pembatalan", t.CancellationCount.ToString(CultureInfo.InvariantCulture), width));
        lines.Add(rule);

        lines.AddRange(Signatures(data.CashierName, width));
        return lines;
    }

    /// <summary>Two columns: "Kasir," and "Supervisor,", blank space to sign, a line each, the cashier's name.</summary>
    private static IEnumerable<string> Signatures(string cashierName, int width)
    {
        var column = width / 2;
        string Pair(string left, string right) => (Center(left, column) + Center(right, width - column)).TrimEnd();

        yield return "";
        yield return Pair("Kasir,", "Supervisor,");
        for (var i = 0; i < SignatureBlankLines; i++)
            yield return "";
        var line = "(" + new string('_', Math.Max(4, column - 4)) + ")";
        yield return Pair(line, line);
        yield return Pair(ReceiptFormatter.Fit(cashierName, column - 1), "");
    }

    private static string Center(string text, int width)
    {
        text = ReceiptFormatter.Fit(text, width);
        var left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }

    private static IEnumerable<string> Row(string label, decimal value, int width) =>
        ReceiptFormatter.Row(label, ReceiptFormatter.Number(value), width);

    private static string Field(string label, string value, int width) =>
        ReceiptFormatter.Fit($"{label.PadRight(FieldLabelWidth)}: {value}", width);

    private static string Time(DateTime local) => local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
