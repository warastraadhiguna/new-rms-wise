using System.Globalization;
using System.Text;

namespace Pos.Shared.Printing;

/// <summary>
/// ESC/POS bytes for receipt printers such as the EPSON TM-T82II, sent RAW through the Windows spooler.
/// Text is printed in the printer's default font (Font A: 48 columns on 80 mm, 32 on 58 mm) as plain ASCII.
/// </summary>
public static class EscPos
{
    /// <summary>ESC @: reset the printer.</summary>
    public static readonly byte[] Initialize = [0x1B, 0x40];

    /// <summary>ESC p 0 25 250: pulse drawer pin 2 for 50 ms, then wait 500 ms (standard cash drawer).</summary>
    public static readonly byte[] DrawerPulse = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    /// <summary>GS V 66 0: feed to the cutter and cut (partial cut on TM-T82II).</summary>
    public static readonly byte[] FeedAndCut = [0x1D, 0x56, 0x42, 0x00];

    public const byte LineFeed = 0x0A;

    /// <summary>Init, optional drawer pulse (cash sales: the drawer opens while the receipt prints), lines, cut.</summary>
    public static byte[] Receipt(IEnumerable<string> lines, bool openDrawer)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var bytes = new List<byte>(2048);
        bytes.AddRange(Initialize);
        if (openDrawer)
            bytes.AddRange(DrawerPulse);
        foreach (var line in lines)
        {
            bytes.AddRange(ToAscii(line));
            bytes.Add(LineFeed);
        }
        bytes.AddRange(FeedAndCut);
        return [.. bytes];
    }

    /// <summary>Only opens the cash drawer.</summary>
    public static byte[] OpenDrawer() => [.. Initialize, .. DrawerPulse];

    /// <summary>Accents removed ("é" → "e"); other non-ASCII and control characters become "?".</summary>
    public static byte[] ToAscii(string text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var result = new byte[decomposed.Length];
        var length = 0;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            result[length++] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }
        return result[..length];
    }
}
