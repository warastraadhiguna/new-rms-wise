using System.Globalization;

namespace Pos.Desktop.Services;

/// <summary>
/// A location transfer as monospace lines (detail from Daftar Mutasi Lokasi and from the Kartu Stok): header, then per
/// line the product, the quantity as typed (e.g. 2 BOX = 48 PCS) and the expiry dates it took (FEFO).
/// </summary>
public static class TransferDetailText
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public static IReadOnlyList<string> Lines(LocationTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var lines = new List<string>
        {
            $"No Mutasi : {transfer.Number}",
            $"Tanggal   : {BranchTime.ToLocal(transfer.TransferredAt, transfer.TimeZone):dd/MM/yyyy HH:mm}",
            $"Dari      : {transfer.FromLocationCode}",
            $"Ke        : {transfer.ToLocationCode}",
            $"Oleh      : {transfer.CreatedByName}",
        };
        if (!string.IsNullOrWhiteSpace(transfer.Notes))
            lines.Add($"Keterangan: {transfer.Notes}");
        lines.Add(new string('-', 48));

        foreach (var line in transfer.Lines)
        {
            lines.Add($"{line.LineNo}. {line.Sku} {line.Name}");
            var typed = line.Qty is { } qty ? $"{Number(qty)} {line.UnitCode}" : $"{Number(line.BaseQty)} {line.BaseUnitCode}";
            lines.Add(line.Qty is not null && line.UnitCode != line.BaseUnitCode
                ? $"   {typed} = {Number(line.BaseQty)} {line.BaseUnitCode}"
                : $"   {typed}");
            foreach (var expiry in line.Expiries)
            {
                var date = expiry.ExpiryDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Tanpa expired";
                lines.Add($"     {date,-14} {Number(expiry.BaseQty),10} {line.BaseUnitCode}");
            }
        }
        return lines;
    }

    private static string Number(decimal value) => value.ToString("#,0.####", Indonesian);
}
