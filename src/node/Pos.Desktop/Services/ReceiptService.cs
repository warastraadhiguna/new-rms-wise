using System.IO;
using Pos.Shared.Printing;

namespace Pos.Desktop.Services;

/// <summary>Sends RAW bytes to a Windows printer.</summary>
public interface IReceiptPrinter
{
    /// <exception cref="PrinterException">The printer is unknown, offline or refused the job.</exception>
    void PrintRaw(string printerName, byte[] data, string documentName);
}

public sealed class PrinterException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The last sale paid on this device (F5 "Cetak Ulang"). Only the id is kept; the data comes from the server.</summary>
public interface ILastSaleStore
{
    Guid? Load();

    void Save(Guid saleId);
}

public sealed class FileLastSaleStore(string path) : ILastSaleStore
{
    private readonly JsonFileStore<LastSale> _file = new(path);

    public static string PathFor(Guid? deviceId) =>
        Path.Combine(JsonFileStore<LastSale>.DefaultFolder, $"last-sale-{deviceId?.ToString() ?? "unknown"}.json");

    public Guid? Load() => _file.Load()?.SaleId;

    public void Save(Guid saleId) => _file.Save(new LastSale(saleId));

    public sealed record LastSale(Guid SaleId);
}

/// <param name="PrinterName">Empty = no printer: the receipt is shown on screen.</param>
/// <param name="Width">Characters per line (48 for 80 mm, 32 for 58 mm).</param>
public sealed record ReceiptPrinterSettings(string? PrinterName, int Width = ReceiptFormatter.Width80mm);

/// <param name="Printed">Sent to the printer.</param>
/// <param name="Number">Nota number, when the sale could be loaded.</param>
/// <param name="Message">What went wrong (Indonesian), null when printed.</param>
public sealed record ReceiptPrintResult(bool Printed, string? Number, string? Message);

/// <summary>
/// Prints a stored sale: loads it from the Node Service, lays it out with <see cref="ReceiptFormatter"/> and sends
/// ESC/POS to the configured printer (drawer pulse for cash, not on reprints). Without a printer, or when printing
/// fails, the receipt is shown on screen with a clear message. Never throws: the sale is already saved.
/// </summary>
public sealed class ReceiptService(
    INodeApi api, IReceiptPrinter printer, IDialogService dialogs, ReceiptPrinterSettings settings, ILastSaleStore lastSale)
{
    public Guid? LastSaleId => lastSale.Load();

    /// <summary>Best effort: a failed write only means F5 cannot find this nota.</summary>
    public void RememberLastSale(Guid saleId)
    {
        try
        {
            lastSale.Save(saleId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<ReceiptPrintResult> PrintAsync(Guid saleId, bool reprint, CancellationToken ct = default)
    {
        SaleDetail sale;
        try
        {
            sale = await api.GetSaleAsync(saleId, ct);
        }
        catch (NodeApiException ex)
        {
            return new ReceiptPrintResult(false, null,
                $"Struk tidak dapat dicetak: {ex.Message} Penjualan tetap tersimpan; tekan F5 untuk cetak ulang.");
        }

        var lines = ReceiptFormatter.Format(ToReceiptData(sale), settings.Width, reprint);

        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            const string message = "Printer struk belum diatur (printer_name di settings.json). Struk ditampilkan di layar.";
            dialogs.ShowReceiptPreview(message, lines);
            return new ReceiptPrintResult(false, sale.Number, message);
        }

        var openDrawer = !reprint && sale.Payments.Any(p => p.IsCash);
        try
        {
            printer.PrintRaw(settings.PrinterName, EscPos.Receipt(lines, openDrawer), $"Struk {sale.Number}");
            return new ReceiptPrintResult(true, sale.Number, null);
        }
        catch (PrinterException ex)
        {
            var message = $"Gagal mencetak ke printer \"{settings.PrinterName}\": {ex.Message} " +
                          "Struk ditampilkan di layar; penjualan tetap tersimpan." +
                          (openDrawer ? " Laci kas tidak terbuka otomatis." : "");
            dialogs.ShowReceiptPreview(message, lines);
            return new ReceiptPrintResult(false, sale.Number, message);
        }
    }

    /// <summary>Server sale → receipt fields; the time is shown in the selling branch's timezone.</summary>
    public static ReceiptData ToReceiptData(SaleDetail sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new ReceiptData(
            sale.Receipt.HeaderLines,
            sale.Receipt.FooterLines,
            sale.Number,
            ToBranchTime(sale.SoldAt, sale.TimeZone),
            sale.Cashier.FullName,
            sale.Customer is { } c ? $"{c.Code} - {c.Name}" : null,
            sale.Lines.Select(l => new ReceiptItem(l.Name, l.Qty, l.UnitCode, l.UnitPrice, l.LineTotal)).ToList(),
            sale.Subtotal,
            sale.DiscountAmount,
            sale.DiscountPct,
            sale.RoundingAmount,
            sale.Total,
            sale.Payments.Select(p => new ReceiptPayment(p.MethodName, p.IsCash, p.IsCredit, p.Amount, p.Tendered, p.ChangeGiven)).ToList(),
            sale.DueDate);
    }

    private static DateTime ToBranchTime(DateTime soldAt, string timeZone)
    {
        var utc = soldAt.Kind == DateTimeKind.Utc ? soldAt : DateTime.SpecifyKind(soldAt.ToUniversalTime(), DateTimeKind.Utc);
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return utc.ToLocalTime();
        }
    }
}
