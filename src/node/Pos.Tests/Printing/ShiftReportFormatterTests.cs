using Pos.Shared.Printing;
using Pos.Shared.Sales;

namespace Pos.Tests.Printing;

public class ShiftReportFormatterTests
{
    private static ShiftReportData Report(decimal counted = 400000m, string? closedBy = null, string? notes = null) => new(
        ["TOKO DEV", "Jl. Merdeka No. 1"],
        "TK01-SH-202611-000007",
        "Kasir Satu",
        new DateTime(2026, 11, 22, 7, 0, 0),
        new DateTime(2026, 11, 22, 15, 5, 0),
        closedBy,
        notes,
        new ShiftTotals(200000m, 137500m, 50000m, 25000m, 12000m,
            [
                new ShiftMethodTotal("DEBIT", "DEBIT", 85000m, 2), new ShiftMethodTotal("QRIS", "QRIS", 0m, 0),
                new ShiftMethodTotal("TRANSFER", "TRANSFER", 1250000m, 1), new ShiftMethodTotal("CREDIT", "KREDIT", 300000m, 1),
            ],
            SaleCount: 14, CancellationCount: 2),
        ExpectedCash: 400500m,
        CountedCash: counted);

    [Theory]
    [InlineData(ReceiptFormatter.Width80mm)]
    [InlineData(ReceiptFormatter.Width58mm)]
    public void No_line_is_wider_than_the_paper(int width)
    {
        Assert.All(ShiftReportFormatter.Format(Report(notes: "uang kecil kurang karena kembalian banyak sekali hari ini"), width),
            line => Assert.True(line.Length <= width, $"'{line}' is {line.Length} > {width}"));
    }

    [Fact]
    public void Shows_header_shift_and_every_summary_figure()
    {
        var lines = ShiftReportFormatter.Format(Report());

        Assert.Equal("TOKO DEV", lines[0].Trim());
        Assert.Contains(lines, l => l.Trim() == "LAPORAN TUTUP SHIFT");
        Assert.Contains("No Shift : TK01-SH-202611-000007", lines);
        Assert.Contains("Kasir    : Kasir Satu", lines);
        Assert.Contains("Dibuka   : 22/11/2026 07:00", lines);
        Assert.Contains("Ditutup  : 22/11/2026 15:05", lines);
        Assert.Contains(Row("Modal Awal", "200.000"), lines);
        Assert.Contains(Row("Penjualan Tunai", "137.500"), lines);
        Assert.Contains(Row("Uang Muka Kredit", "50.000"), lines);
        Assert.Contains(Row("Pelunasan Piutang Tunai", "25.000"), lines);
        Assert.Contains(Row("Pengeluaran Kas", "-12.000"), lines);
        Assert.Contains(Row("Kas Seharusnya", "400.500"), lines);
        Assert.Contains(Row("Kas Fisik", "400.000"), lines);
        Assert.Contains(Row("Selisih (Kurang)", "-500"), lines);
        Assert.Contains(Row("DEBIT (2)", "85.000"), lines);
        Assert.Contains(Row("QRIS (0)", "0"), lines);
        Assert.Contains(Row("TRANSFER (1)", "1.250.000"), lines);
        Assert.Contains(Row("KREDIT (1)", "300.000"), lines);
        Assert.Contains(Row("Jumlah Nota", "14"), lines);
        Assert.Contains(Row("Jumlah Pembatalan", "2"), lines);
        Assert.DoesNotContain(lines, l => l.Contains("Ditutup oleh"));
        Assert.DoesNotContain(lines, l => l.Contains("CETAK ULANG"));
    }

    [Theory]
    [InlineData("400500", "Selisih (Pas)", "0")]
    [InlineData("401000", "Selisih (Lebih)", "500")]
    public void Difference_says_lebih_kurang_or_pas(string counted, string label, string value)
    {
        var lines = ShiftReportFormatter.Format(Report(decimal.Parse(counted, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Contains(Row(label, value), lines);
    }

    [Fact]
    public void Closed_by_someone_else_shows_who_and_why()
    {
        var lines = ShiftReportFormatter.Format(Report(closedBy: "Admin Toko Dev", notes: "kasir pulang sakit"));

        Assert.Contains("Ditutup oleh: Admin Toko Dev", lines);
        Assert.Contains("Keterangan: kasir pulang sakit", lines);
    }

    [Fact]
    public void Ends_with_signature_places_for_cashier_and_supervisor()
    {
        var lines = ShiftReportFormatter.Format(Report());

        var titles = lines.Single(l => l.Contains("Kasir,") && l.Contains("Supervisor,"));
        Assert.True(titles.IndexOf("Kasir,", StringComparison.Ordinal) < titles.IndexOf("Supervisor,", StringComparison.Ordinal));
        var index = lines.ToList().IndexOf(titles);
        var signLine = lines.Skip(index + 1).First(l => l.Contains("(___"));
        Assert.Equal(2, signLine.Split("(___", StringSplitOptions.None).Length - 1);
        Assert.Contains(lines.Skip(index + 1), l => l.Contains("Kasir Satu"));
        Assert.True(lines.Skip(index + 1).TakeWhile(l => !l.Contains("(___")).Count(string.IsNullOrWhiteSpace) >= 3);
    }

    [Fact]
    public void Reprint_is_marked()
    {
        Assert.Contains(ShiftReportFormatter.Format(Report(), reprint: true), l => l.Trim() == "CETAK ULANG");
    }

    private static string Row(string label, string value) => label + value.PadLeft(ReceiptFormatter.Width80mm - label.Length);
}
