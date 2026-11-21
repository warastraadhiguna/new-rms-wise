using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Pos.Desktop.Services;

/// <summary>
/// "Export to File [F8]": a minimal Excel workbook (Office Open XML, one sheet) without extra libraries.
/// Text is written as inline strings, numbers (decimal, int, long, double) as real numbers so Excel can sum them,
/// dates as dd/MM/yyyy text. The header row is bold and frozen.
/// </summary>
public static class XlsxWriter
{
    public static void Write(string path, string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Entry(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Entry(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Entry(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>""");
            WriteWorkbook(zip, sheetName);
            WriteSheet(zip, headers, rows);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteWorkbook(ZipArchive zip, string sheetName)
    {
        using var stream = zip.CreateEntry("xl/workbook.xml").Open();
        using var xml = XmlWriter.Create(stream, Settings);
        xml.WriteStartDocument(true);
        xml.WriteStartElement("workbook", Main);
        xml.WriteAttributeString("xmlns", "r", null, Relationships);
        xml.WriteStartElement("sheets", Main);
        xml.WriteStartElement("sheet", Main);
        xml.WriteAttributeString("name", SafeSheetName(sheetName));
        xml.WriteAttributeString("sheetId", "1");
        xml.WriteAttributeString("id", Relationships, "rId1");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteSheet(ZipArchive zip, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
        using var xml = XmlWriter.Create(stream, Settings);
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", Main);

        // freeze the header row
        xml.WriteStartElement("sheetViews", Main);
        xml.WriteStartElement("sheetView", Main);
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane", Main);
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("cols", Main);
        for (var i = 0; i < headers.Count; i++)
        {
            xml.WriteStartElement("col", Main);
            xml.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("width", Math.Max(12, headers[i].Length + 4).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();

        xml.WriteStartElement("sheetData", Main);
        WriteRow(xml, 1, headers.Cast<object?>().ToList(), style: 1);
        var rowNumber = 1;
        foreach (var row in rows)
            WriteRow(xml, ++rowNumber, row, style: 0);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteRow(XmlWriter xml, int rowNumber, IReadOnlyList<object?> values, int style)
    {
        xml.WriteStartElement("row", Main);
        xml.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
                continue;
            xml.WriteStartElement("c", Main);
            xml.WriteAttributeString("r", ColumnName(i) + rowNumber.ToString(CultureInfo.InvariantCulture));
            if (style != 0)
                xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));

            if (Number(values[i]) is { } number)
            {
                xml.WriteElementString("v", Main, number);
            }
            else
            {
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteStartElement("is", Main);
                xml.WriteStartElement("t", Main);
                xml.WriteAttributeString("xml", "space", null, "preserve");
                xml.WriteString(CleanText(Text(values[i]!)));
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static string? Number(object? value) => value switch
    {
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        long n => n.ToString(CultureInfo.InvariantCulture),
        double n when double.IsFinite(n) => n.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Text(object value) => value switch
    {
        DateOnly d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "Ya" : "Tidak",
        _ => value.ToString() ?? "",
    };

    /// <summary>A, B, ..., Z, AA, AB, ...</summary>
    public static string ColumnName(int index)
    {
        var name = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name.Insert(0, (char)('A' + (n - 1) % 26));
        return name.ToString();
    }

    /// <summary>XML cannot hold most control characters.</summary>
    private static string CleanText(string text) =>
        new(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());

    private static string SafeSheetName(string name)
    {
        var cleaned = new string((name ?? "Sheet1").Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        return cleaned.Length == 0 ? "Sheet1" : cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false) };
}
