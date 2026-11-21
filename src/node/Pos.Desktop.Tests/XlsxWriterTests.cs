using System.Globalization;
using ClosedXML.Excel;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

/// <summary>One non-empty cell read back from an exported workbook.</summary>
/// <param name="Text">Text as written; numbers in invariant culture ("2.5"); dates as shown in Excel ("02/12/2026").</param>
public sealed record XlsxCell(string Ref, string Text, bool IsNumber, bool IsDate, bool IsBold);

public class XlsxWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-xlsx-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The used cells of the first sheet, row by row (empty cells skipped), read with ClosedXML.</summary>
    public static List<List<XlsxCell>> ReadSheet(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        return sheet.RowsUsed().Select(row => row.CellsUsed().Select(cell =>
        {
            var value = cell.Value;
            var text = value.IsNumber ? value.GetNumber().ToString(CultureInfo.InvariantCulture)
                : value.IsDateTime ? value.GetDateTime().ToString(
                    cell.Style.DateFormat.Format.Contains("HH", StringComparison.Ordinal) ? XlsxWriter.DateTimeFormat : XlsxWriter.DateFormat,
                    CultureInfo.InvariantCulture)
                : cell.GetString();
            return new XlsxCell(cell.Address.ToString()!, text, value.IsNumber, value.IsDateTime, cell.Style.Font.Bold);
        }).ToList()).ToList();
    }

    private string PathFor(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    [Fact]
    public void Header_is_bold_and_frozen_text_stays_text_and_numbers_are_numbers()
    {
        var path = PathFor("stok.xlsx");

        XlsxWriter.Write(path, "Stok Total", ["Kode Barang", "Nama Barang", "Stok", "Jumlah"],
            [
                ["AIR-600", "Air mineral 600 ml", "1 BOX 14 PCS", 38m],
                ["0001", "=SUM(A1:A2)", "2,5", 2.5m],
                ["X", null, "0", 0],
            ]);

        var rows = ReadSheet(path);
        Assert.Equal(4, rows.Count);
        Assert.All(rows[0], c => Assert.True(c.IsBold));
        Assert.Equal(["Kode Barang", "Nama Barang", "Stok", "Jumlah"], rows[0].Select(c => c.Text));
        Assert.Equal(("D2", "38", true), (rows[1][3].Ref, rows[1][3].Text, rows[1][3].IsNumber));
        Assert.False(rows[1][0].IsBold);
        Assert.Equal("0001", rows[2][0].Text);                                     // leading zeros kept: text, not a number
        Assert.False(rows[2][0].IsNumber);
        Assert.Equal("=SUM(A1:A2)", rows[2][1].Text);                              // never turned into a formula
        Assert.Equal("2.5", rows[2][3].Text);
        Assert.Equal(["A4", "C4", "D4"], rows[3].Select(c => c.Ref));               // empty cell skipped
        Assert.False(File.Exists(path + ".tmp"));

        using var workbook = new XLWorkbook(path);
        Assert.Equal("Stok Total", workbook.Worksheet(1).Name);
        Assert.Equal(1, workbook.Worksheet(1).SheetView.SplitRow);                 // header frozen
    }

    [Fact]
    public void Special_characters_round_trip()
    {
        var path = PathFor("khusus.xlsx");
        const string special = "Beras <curah> & \"premium\" 'asli' — kualitas №1 · 日本 · emoji 🍚";

        XlsxWriter.Write(path, "Khusus", ["Nama"], [[special], ["baris\nbaru\tdan tab"], ["kontrol\u0001\u001Fdibuang"]]);

        var rows = ReadSheet(path);
        Assert.Equal(special, rows[1][0].Text);
        Assert.Equal("baris\nbaru\tdan tab", rows[2][0].Text);
        Assert.Equal("kontroldibuang", rows[3][0].Text);
    }

    [Fact]
    public void Large_and_small_numbers_round_trip()
    {
        var path = PathFor("angka.xlsx");

        XlsxWriter.Write(path, "Angka", ["Nilai"],
            [[99999999890000m], [1563125000m], [-26m], [0.0001m], [12345.6789m], [int.MaxValue], [999_999_999_999_999L]]);

        var values = ReadSheet(path).Skip(1).Select(r => decimal.Parse(r[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
        // Excel stores numbers as doubles: up to 15 significant digits round-trip exactly (store quantities are far below).
        Assert.Equal([99999999890000m, 1563125000m, -26m, 0.0001m, 12345.6789m, int.MaxValue, 999_999_999_999_999m], values);
        Assert.All(ReadSheet(path).Skip(1), r => Assert.True(r[0].IsNumber));
    }

    [Fact]
    public void Dates_and_date_times_are_real_excel_dates_shown_the_indonesian_way()
    {
        var path = PathFor("tanggal.xlsx");

        XlsxWriter.Write(path, "Tanggal", ["Tanggal", "Waktu"], [[new DateOnly(2026, 12, 2), new DateTime(2026, 11, 20, 9, 5, 0)]]);

        var row = ReadSheet(path)[1];
        Assert.Equal(("02/12/2026", true), (row[0].Text, row[0].IsDate));
        Assert.Equal(("20/11/2026 09:05", true), (row[1].Text, row[1].IsDate));
        using var workbook = new XLWorkbook(path);
        Assert.Equal(XlsxWriter.DateFormat, workbook.Worksheet(1).Cell("A2").Style.DateFormat.Format);
    }

    [Fact]
    public void Bad_sheet_names_are_cleaned_and_existing_files_are_replaced()
    {
        var path = PathFor("cek.xlsx");
        File.WriteAllText(path, "lama");

        XlsxWriter.Write(path, "Cek/Stok:[1]", ["A"], [["x"]]);

        using var workbook = new XLWorkbook(path);
        Assert.Equal("CekStok1", workbook.Worksheet(1).Name);
        Assert.Equal("x", workbook.Worksheet(1).Cell("A2").GetString());
    }
}
