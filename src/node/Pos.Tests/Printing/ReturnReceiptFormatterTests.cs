using Pos.Shared.Printing;
using Pos.Shared.Sales;

namespace Pos.Tests.Printing;

public class ReturnReceiptFormatterTests
{
    private static ReturnReceiptData Receipt(string settlement = ReturnSettlements.Refund) => new(
        ["TOKO DEV", "Jl. Merdeka No. 1"],
        "TK01-RT-202611-000004",
        new DateTime(2026, 11, 22, 16, 45, 0),
        "TK01-SL-202611-000123",
        "Kasir Satu",
        "C000001 - Budi Pelanggan",
        [
            new ReturnReceiptItem("2023_amplop lebaran karakter isi 10 lembar warna emas", 2m, "PCS", 6750m, 13500m, Damaged: false),
            new ReturnReceiptItem("Teh kotak", 1m, "PCS", 3600m, 3600m, Damaged: true),
        ],
        17100m,
        settlement,
        "Barang rusak",
        "kemasan sobek");

    [Theory]
    [InlineData(ReceiptFormatter.Width80mm)]
    [InlineData(ReceiptFormatter.Width58mm)]
    public void No_line_is_wider_than_the_paper(int width)
    {
        Assert.All(ReturnReceiptFormatter.Format(Receipt(), width), l => Assert.True(l.Length <= width, $"'{l}' > {width}"));
    }

    [Fact]
    public void Shows_return_nota_items_total_and_settlement()
    {
        var lines = ReturnReceiptFormatter.Format(Receipt());

        Assert.Equal("TOKO DEV", lines[0].Trim());
        Assert.Contains(lines, l => l.Trim() == "BUKTI RETUR PENJUALAN");
        Assert.Contains("No Retur : TK01-RT-202611-000004", lines);
        Assert.Contains("Tanggal  : 22/11/2026 16:45", lines);
        Assert.Contains("No Nota  : TK01-SL-202611-000123", lines);
        Assert.Contains("Kasir    : Kasir Satu", lines);
        Assert.Contains("Member   : C000001 - Budi Pelanggan", lines);
        Assert.Contains(lines, l => l.StartsWith("  2 PCS x 6.750", StringComparison.Ordinal) && l.EndsWith("13.500", StringComparison.Ordinal));
        Assert.Contains("  Kondisi: RUSAK", lines);
        Assert.Single(lines, l => l.Contains("Kondisi"));                           // BAIK is not printed
        Assert.Contains(Row("Total Retur", "17.100"), lines);
        Assert.Contains(Row("Penyelesaian", "Uang Kembali"), lines);
        Assert.Contains("Alasan: Barang rusak - kemasan sobek", lines);
        Assert.Contains(lines, l => l.Contains("Pelanggan,") && l.Contains("Kasir,"));
    }

    [Theory]
    [InlineData(ReturnSettlements.Exchange, "Tukar Barang")]
    [InlineData(ReturnSettlements.ReduceReceivable, "Potong Piutang")]
    public void Settlement_label(string settlement, string label)
    {
        Assert.Contains(Row("Penyelesaian", label), ReturnReceiptFormatter.Format(Receipt(settlement)));
    }

    [Fact]
    public void Reprint_is_marked()
    {
        Assert.Contains(ReturnReceiptFormatter.Format(Receipt(), reprint: true), l => l.Trim() == "CETAK ULANG");
    }

    private static string Row(string label, string value) => label + value.PadLeft(ReceiptFormatter.Width80mm - label.Length);
}
