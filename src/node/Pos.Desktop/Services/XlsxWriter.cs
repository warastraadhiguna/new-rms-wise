using System.IO;
using ClosedXML.Excel;

namespace Pos.Desktop.Services;

/// <summary>
/// "Export to File [F8]": one-sheet Excel workbook written with ClosedXML. Text stays text (never a formula),
/// numbers (decimal, int, long, double) are real numbers so Excel can sum them, dates are real dates shown as
/// dd/MM/yyyy (dd/MM/yyyy HH:mm with a time). The header row is bold and frozen.
/// </summary>
public static class XlsxWriter
{
    public const string DateFormat = "dd/MM/yyyy";
    public const string DateTimeFormat = "dd/MM/yyyy HH:mm";

    public static void Write(string path, string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(sheetName));

        for (var c = 0; c < headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
            sheet.Column(c + 1).Width = Math.Max(12, headers[c].Length + 4);
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var r = 1;
        foreach (var row in rows)
        {
            r++;
            for (var c = 0; c < row.Count; c++)
                Set(sheet.Cell(r, c + 1), row[c]);
        }

        // write next to the target first, so a failed export never leaves half a file under the chosen name
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            workbook.SaveAs(stream, validate: false);
        File.Move(temp, path, overwrite: true);
    }

    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case decimal d:
                cell.Value = (double)d;
                break;
            case int n:
                cell.Value = n;
                break;
            case long n:
                cell.Value = n;
                break;
            case double n when double.IsFinite(n):
                cell.Value = n;
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = DateFormat;
                break;
            case DateTime t:
                cell.Value = t;
                cell.Style.DateFormat.Format = DateTimeFormat;
                break;
            case bool b:
                cell.Value = b ? "Ya" : "Tidak";
                break;
            default:
                cell.Value = CleanText(value.ToString() ?? "");
                break;
        }
    }

    /// <summary>XML cannot hold most control characters.</summary>
    private static string CleanText(string text) =>
        new(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());

    /// <summary>Excel sheet names: no \ / ? * [ ] :, at most 31 characters, never empty.</summary>
    private static string SafeSheetName(string name)
    {
        var cleaned = new string((name ?? "").Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        return cleaned.Length == 0 ? "Sheet1" : cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
