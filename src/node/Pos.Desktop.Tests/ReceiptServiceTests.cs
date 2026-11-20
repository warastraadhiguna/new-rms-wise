using System.Text;
using Pos.Desktop.Services;
using Pos.Shared.Printing;

namespace Pos.Desktop.Tests;

public class ReceiptServiceTests
{
    private static readonly byte[] DrawerPulse = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    private readonly FakeNodeApi _api = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakePrinter _printer = new();
    private readonly Guid _saleId = Guid.CreateVersion7();

    private ReceiptService Service(string? printerName = "EPSON TM-T82II Receipt", int width = 48) =>
        Receipts.Service(_api, _dialogs, _printer, printerName, width);

    private static string Text(byte[] data) => Encoding.ASCII.GetString(data);

    private static bool Contains(byte[] data, byte[] part) =>
        Enumerable.Range(0, data.Length - part.Length + 1).Any(i => data.AsSpan(i, part.Length).SequenceEqual(part));

    [Fact]
    public async Task Cash_sale_prints_escpos_with_drawer_pulse_and_cut()
    {
        var result = await Service().PrintAsync(_saleId, reprint: false);

        Assert.True(result.Printed);
        Assert.Equal("TK01-SL-202611-000123", result.Number);
        Assert.Contains($"get-sale:{_saleId}", _api.Calls);
        var job = Assert.Single(_printer.Jobs);
        Assert.Equal("EPSON TM-T82II Receipt", job.Printer);
        Assert.Equal("Struk TK01-SL-202611-000123", job.Document);
        Assert.Equal([.. EscPos.Initialize, .. EscPos.DrawerPulse], job.Data[..7]);
        Assert.Equal(EscPos.FeedAndCut, job.Data[^4..]);
        Assert.Contains("TOKO DEV", Text(job.Data));
        Assert.Contains("Tanggal: 20/11/2026 18:05", Text(job.Data));     // 11:05 UTC in Asia/Jakarta
        Assert.Contains("2023_amplop lebaran karakter", Text(job.Data));
        Assert.Empty(_dialogs.Previews);
    }

    [Fact]
    public async Task Non_cash_sale_does_not_open_the_drawer()
    {
        _api.OnGetSale = id => Receipts.Detail(id, cash: false);

        await Service().PrintAsync(_saleId, reprint: false);

        Assert.False(Contains(Assert.Single(_printer.Jobs).Data, DrawerPulse));
    }

    [Fact]
    public async Task Reprint_says_cetak_ulang_and_never_opens_the_drawer()
    {
        await Service().PrintAsync(_saleId, reprint: true);

        var data = Assert.Single(_printer.Jobs).Data;
        Assert.Contains("CETAK ULANG", Text(data));
        Assert.False(Contains(data, DrawerPulse));
    }

    [Fact]
    public async Task Without_a_printer_the_receipt_is_shown_on_screen()
    {
        var result = await Service(printerName: null).PrintAsync(_saleId, reprint: false);

        Assert.False(result.Printed);
        Assert.Empty(_printer.Jobs);
        var (message, lines) = Assert.Single(_dialogs.Previews);
        Assert.Contains("printer_name", message);
        Assert.Equal(message, result.Message);
        Assert.Equal(new string('-', 48), lines[2]);
        Assert.Contains(lines, l => l.StartsWith("Grand Total", StringComparison.Ordinal) && l.EndsWith("19.500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_uses_the_configured_paper_width()
    {
        await Service(printerName: "", width: 32).PrintAsync(_saleId, reprint: false);

        var lines = Assert.Single(_dialogs.Previews).Lines;
        Assert.All(lines, l => Assert.True(l.Length <= 32));
        Assert.Contains(new string('-', 32), lines);
    }

    [Fact]
    public async Task Printer_failure_shows_the_receipt_and_a_clear_message()
    {
        _printer.Failure = "printer tidak ditemukan atau tidak dapat dibuka (offline).";

        var result = await Service().PrintAsync(_saleId, reprint: false);

        Assert.False(result.Printed);
        var (message, lines) = Assert.Single(_dialogs.Previews);
        Assert.Contains("Gagal mencetak ke printer \"EPSON TM-T82II Receipt\"", message);
        Assert.Contains("offline", message);
        Assert.Contains("penjualan tetap tersimpan", message);
        Assert.Contains("Laci kas tidak terbuka", message);
        Assert.Contains("TOKO DEV", lines[0]);
    }

    [Fact]
    public async Task Credit_sale_with_dp_prints_uang_muka_sisa_piutang_and_jatuh_tempo_and_opens_the_drawer()
    {
        _api.OnGetSale = id => Receipts.Detail(id) with
        {
            Customer = new SaleCustomer(Guid.CreateVersion7(), "C000001", "Budi Pelanggan"),
            Total = 19500m,
            CreditAmount = 14500m,
            Change = 0m,
            DueDate = new DateOnly(2026, 12, 4),
            Payments =
            [
                new SaleDetailPayment("CASH", "TUNAI", true, false, 5000m, 5000m, 0m, null),
                new SaleDetailPayment("CREDIT", "KREDIT", false, true, 14500m, null, null, null),
            ],
        };

        await Service().PrintAsync(_saleId, reprint: false);

        var data = Assert.Single(_printer.Jobs).Data;
        var text = Text(data);
        Assert.Contains("Member : C000001 - Budi Pelanggan", text);
        Assert.Matches(@"Uang Muka +5\.000\n", text);
        Assert.Matches(@"Sisa Piutang +14\.500\n", text);
        Assert.Matches(@"Jatuh Tempo +04/12/2026\n", text);
        Assert.DoesNotContain("Kembali", text);
        Assert.True(Contains(data, DrawerPulse));            // the DP is cash
    }

    [Fact]
    public async Task Sale_that_cannot_be_loaded_gives_a_message_and_no_preview()
    {
        _api.OnGetSale = _ => throw new NodeApiException("Node Service tidak dapat dihubungi.", null, null);

        var result = await Service().PrintAsync(_saleId, reprint: false);

        Assert.False(result.Printed);
        Assert.Contains("F5", result.Message);
        Assert.Empty(_dialogs.Previews);
        Assert.Empty(_printer.Jobs);
    }

    [Fact]
    public void Receipt_data_maps_member_discount_and_credit()
    {
        var detail = Receipts.Detail(_saleId) with
        {
            Customer = new SaleCustomer(Guid.CreateVersion7(), "C000001", "Budi Pelanggan"),
            DiscountAmount = 1950m,
            DiscountPct = 10m,
            DueDate = new DateOnly(2026, 12, 4),
            Payments = [new SaleDetailPayment("CREDIT", "KREDIT", false, true, 17550m, null, null, null)],
        };

        var data = ReceiptService.ToReceiptData(detail);

        Assert.Equal("C000001 - Budi Pelanggan", data.CustomerName);
        Assert.Equal(10m, data.DiscountPct);
        Assert.Equal(new DateOnly(2026, 12, 4), data.DueDate);
        Assert.Equal("KREDIT", Assert.Single(data.Payments).MethodName);
        Assert.Equal(new DateTime(2026, 11, 20, 18, 5, 0), data.SoldAtLocal);
        Assert.Equal("Kasir Satu", data.CashierName);
    }

    [Fact]
    public void Last_sale_is_remembered_per_device_and_a_failed_write_is_ignored()
    {
        var store = new MemoryLastSaleStore();
        var service = Receipts.Service(_api, _dialogs, lastSale: store);

        service.RememberLastSale(_saleId);

        Assert.Equal(_saleId, service.LastSaleId);
        Receipts.Service(_api, _dialogs, lastSale: new ThrowingStore()).RememberLastSale(_saleId);   // no exception
    }

    [Fact]
    public void File_store_keeps_the_last_sale_between_runs()
    {
        var path = Path.Combine(Path.GetTempPath(), "pos-desktop-tests", Guid.NewGuid().ToString("N"), "last-sale.json");
        try
        {
            new FileLastSaleStore(path).Save(_saleId);

            Assert.Equal(_saleId, new FileLastSaleStore(path).Load());
            Assert.EndsWith($"last-sale-{Samples.DeviceId}.json", FileLastSaleStore.PathFor(Samples.DeviceId));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed class ThrowingStore : ILastSaleStore
    {
        public Guid? Load() => null;
        public void Save(Guid saleId) => throw new IOException("disk full");
    }
}
