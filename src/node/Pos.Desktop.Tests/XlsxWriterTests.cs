using System.IO.Compression;
using System.Xml.Linq;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

public class XlsxWriterTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-xlsx-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Cells of the sheet as (reference, type, style, text) by row.</summary>
    public static List<List<(string Ref, string? Type, string? Style, string Text)>> ReadSheet(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(stream);
        return sheet.Descendants(Main + "row").Select(row => row.Elements(Main + "c").Select(c => (
            (string)c.Attribute("r")!,
            (string?)c.Attribute("t"),
            (string?)c.Attribute("s"),
            c.Attribute("t")?.Value == "inlineStr" ? (string)c.Element(Main + "is")!.Element(Main + "t")! : (string)c.Element(Main + "v")!)).ToList()).ToList();
    }

    [Fact]
    public void Writes_a_workbook_excel_can_open_with_bold_header_text_and_numbers()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "stok.xlsx");

        XlsxWriter.Write(path, "Stok Total", ["Kode Barang", "Nama Barang", "Stok", "Jumlah"],
            [
                ["AIR-600", "Air mineral 600 ml", "1 BOX 14 PCS", 38m],
                ["BERAS-KG", "Beras <curah> & \"premium\"", "2,5", 2.5m],
                ["X", null, "0", 0],
            ]);

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/styles.xml"));
            using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
            Assert.Equal("Stok Total", (string)XDocument.Load(workbook).Descendants(Main + "sheet").Single().Attribute("name")!);
        }

        var rows = ReadSheet(path);
        Assert.Equal(4, rows.Count);
        Assert.All(rows[0], c => Assert.Equal("1", c.Style));                      // bold header
        Assert.Equal(["Kode Barang", "Nama Barang", "Stok", "Jumlah"], rows[0].Select(c => c.Text));
        Assert.Equal(("D2", (string?)null, "38"), (rows[1][3].Ref, rows[1][3].Type, rows[1][3].Text));   // a real number
        Assert.Equal("Beras <curah> & \"premium\"", rows[2][1].Text);              // escaped and read back
        Assert.Equal("2.5", rows[2][3].Text);
        Assert.Equal(["A4", "C4", "D4"], rows[3].Select(c => c.Ref));               // empty cell skipped
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_names(int index, string expected) => Assert.Equal(expected, XlsxWriter.ColumnName(index));

    [Fact]
    public void Dates_are_written_as_indonesian_text_and_bad_sheet_names_are_cleaned()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "cek.xlsx");

        XlsxWriter.Write(path, "Cek/Stok:[1]", ["Tanggal"], [[new DateOnly(2026, 12, 2)]]);

        Assert.Equal("02/12/2026", ReadSheet(path)[1][0].Text);
        using var zip = ZipFile.OpenRead(path);
        using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
        Assert.Equal("CekStok1", (string)XDocument.Load(workbook).Descendants(Main + "sheet").Single().Attribute("name")!);
    }
}
