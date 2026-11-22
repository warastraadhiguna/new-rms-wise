using System.Net;
using Pos.Desktop.Configuration;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

/// <summary>
/// The client reads JSON captured from a real Node Service (Contracts/*.json, dev data). If the server's
/// response shape changes, these fail before a cashier does. Refresh the files when the API changes on purpose.
/// </summary>
public class ContractTests
{
    private readonly FakeServer _server = new();
    private readonly SessionState _session = new();

    public ContractTests() => _session.SetLogin(Samples.Login());

    private NodeApiClient Client() => new(
        new HttpClient(_server) { BaseAddress = new Uri("http://kasir-server:5080/") },
        _session, new DesktopSettings("http://kasir-server:5080/", Samples.DeviceId));

    private static string Json(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", name + ".json"));

    [Fact]
    public async Task Sales_config()
    {
        _server.Reply(HttpStatusCode.OK, Json("config"));

        var config = await Client().GetSalesConfigAsync();

        Assert.Equal(["TUNAI", "DEBIT", "QRIS", "TRANSFER", "KREDIT"], config.PaymentMethods.Select(m => m.Name));
        Assert.True(config.PaymentMethods[0].IsCash);
        Assert.True(config.PaymentMethods[4].IsCredit);
        Assert.Equal(100m, config.CashRoundingStep);
        Assert.Equal("nearest", config.CashRoundingMode);
        Assert.Equal(0, config.CurrencyDecimals);
    }

    [Fact]
    public async Task Product_search_and_units()
    {
        _server.Reply(HttpStatusCode.OK, Json("search")).Reply(HttpStatusCode.OK, Json("units"));

        var found = await Client().SearchProductsAsync("8990000000011");
        var units = await Client().GetProductUnitsAsync(found[0].ProductId);

        var water = Assert.Single(found);
        Assert.Equal("AIR-600", water.Sku);
        Assert.Equal(4000m, water.Price);
        Assert.True(water.IsExactMatch);
        Assert.Contains(water.PriceTiers, t => t.MinQty == 12m);
        Assert.Equal(["PCS", "BOX"], units.Select(u => u.UnitCode));
        Assert.Equal(24m, units[1].Conversion);
        Assert.Equal("PCS", units[1].BaseUnitCode);
        Assert.False(units[1].BaseAllowDecimal);
        Assert.Equal("35", units[0].StockText);
        Assert.Equal("1 BOX 11 PCS", units[1].StockText);
    }

    [Fact]
    public async Task Sale_detail_for_the_receipt()
    {
        _server.Reply(HttpStatusCode.OK, Json("sale-detail"));
        var id = Guid.Parse("01a1f8c2-60ec-7a01-871e-7000fe14820e");

        var sale = await Client().GetSaleAsync(id);

        Assert.Equal($"/api/v1/sales/{id}", _server.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("TK01-SL-202611-000006", sale.Number);
        Assert.Equal(DateTimeKind.Utc, sale.SoldAt.Kind);
        Assert.Equal("Kasir Satu", sale.Cashier.FullName);
        Assert.Equal(5m, sale.DiscountPct);
        Assert.Equal(["Air mineral 600 ml", "Gula pasir 1 kg"], sale.Lines.Select(l => l.Name));
        Assert.Equal("TUNAI", Assert.Single(sale.Payments).MethodName);
        Assert.Equal("021-5550123", sale.Receipt.Phone);
        Assert.Equal(["TOKO DEV", "Jl. Merdeka No. 1", "Jakarta", "Telp. 021-5550123"], sale.Receipt.HeaderLines);
        Assert.Equal(["Terimakasih Atas Kunjungannya", "Sampai Jumpa Kembali"], sale.Receipt.FooterLines);

        var lines = Pos.Shared.Printing.ReceiptFormatter.Format(ReceiptService.ToReceiptData(sale), 48);
        Assert.Contains("Tanggal: 20/11/2026 18:40", lines);
        Assert.Contains(lines, l => l.StartsWith("Diskon 5%", StringComparison.Ordinal) && l.EndsWith("1.500", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Kembali", StringComparison.Ordinal) && l.EndsWith("21.500", StringComparison.Ordinal));
        Assert.Equal("         Terimakasih Atas Kunjungannya", lines[^2]);
        Assert.Equal("              Sampai Jumpa Kembali", lines[^1]);
    }

    [Fact]
    public async Task Stock_locations_totals_and_expiry()
    {
        _server.Reply(HttpStatusCode.OK, Json("stock-locations"))
            .Reply(HttpStatusCode.OK, Json("stock-totals"))
            .Reply(HttpStatusCode.OK, Json("stock-expiry"));
        var display = Guid.Parse("01a1f74b-7347-7da5-a0a8-c6043f95ad3f");
        var water = Guid.Parse("01a1f74b-737d-7830-845c-13f0d6777819");

        var locations = await Client().GetStockLocationsAsync();
        var totals = await Client().GetStockTotalsAsync("AIR-600", display, 0, 200);
        var expiry = await Client().GetStockExpiryAsync(water, null);

        Assert.Equal(["DISPLAY", "STORAGE"], locations.Select(l => l.Code));
        Assert.True(locations[0].IsSellable);
        Assert.Equal($"/api/v1/stock/totals?q=AIR-600&offset=0&limit=200&location_id={display}",
            _server.Requests[1].Request.RequestUri!.PathAndQuery);
        var row = Assert.Single(totals.Items);
        Assert.Equal((1, "AIR-600", "BOX", 24m, 11m), (totals.Total, row.Sku, row.DisplayUnitCode, row.DisplayConversion, row.BaseQty));
        Assert.Equal("11 PCS", row.StockText);
        Assert.Equal($"/api/v1/stock/products/{water}/expiry", _server.Requests[2].Request.RequestUri!.PathAndQuery);
        Assert.Equal((30, new DateOnly(2026, 11, 22)), (expiry.WarningDays, expiry.Today));
        var bucket = Assert.Single(expiry.Rows);
        Assert.Equal((new DateOnly(2027, 6, 1), 11m, "ok"), (bucket.ExpiryDate!.Value, bucket.BaseQty, bucket.Status));
    }

    [Fact]
    public async Task Stock_card()
    {
        _server.Reply(HttpStatusCode.OK, Json("stock-card"));
        var water = Guid.Parse("01a1f74b-737d-7830-845c-13f0d6777819");

        var card = await Client().GetStockCardAsync(water, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22), 0, 500);

        Assert.Equal($"/api/v1/stock/card?product_id={water}&from=2026-11-01&to=2026-11-22&offset=0&limit=500",
            _server.Requests[0].Request.RequestUri!.PathAndQuery);
        Assert.Equal(("AIR-600", "Asia/Jakarta", 0m, 11m), (card.Sku, card.TimeZone, card.OpeningBalance, card.ClosingBalance));
        Assert.Equal(card.TotalRows, card.Rows.Count);
        var first = card.Rows[0];
        Assert.Equal(("ADJUSTMENT", "Penyesuaian", "TK01-ADJ-202611-000001", "Admin Dev"), (first.DocType, first.DocLabel, first.DocNumber, first.UserName));
        Assert.Equal(DateTimeKind.Utc, first.OccurredAt.Kind);
        Assert.Equal(card.ClosingBalance, card.Rows[^1].Balance);
        Assert.Contains(card.Rows, r => r is { DocType: "SALE", UserName: "Kasir Satu" } && r.QtyOut > 0);
    }

    [Fact]
    public async Task Location_transfer_preview_create_list_and_detail()
    {
        _server.Reply(HttpStatusCode.OK, Json("transfer-preview"))
            .Reply(HttpStatusCode.Created, Json("transfer"))
            .Reply(HttpStatusCode.OK, Json("transfers"))
            .Reply(HttpStatusCode.OK, Json("transfer"));
        var display = Guid.Parse("01a1f74b-7347-7da5-a0a8-c6043f95ad3f");
        var storage = Guid.Parse("01a1f74b-7347-79b5-8954-4d006912d96f");
        var unit = Guid.Parse("01a1f74b-737d-7577-a6b4-5db0a5317eb3");
        var id = Guid.Parse("01a1ffdf-366d-7ae1-802b-70f5b9f61437");

        var preview = await Client().PreviewLocationTransferAsync(display, [new LocationTransferLineRequest(unit, 2m)]);
        var created = await Client().CreateLocationTransferAsync(new CreateLocationTransferRequest(id, display, storage,
            [new LocationTransferLineRequest(unit, 2m)], "contoh dev"));
        var list = await Client().GetLocationTransfersAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22));
        var detail = await Client().GetLocationTransferAsync(id);

        var line = Assert.Single(preview.Lines);
        Assert.Equal((11m, 11m, 0m, 2m), (line.StockAtSource, line.Available, line.Shortage, line.BaseQty));
        Assert.Equal(new DateOnly(2027, 6, 1), Assert.Single(line.Allocations).ExpiryDate);
        Assert.Contains("\"from_location_id\":\"" + display, _server.Requests[0].Body);
        Assert.Contains("\"product_unit_id\":\"" + unit, _server.Requests[1].Body);
        Assert.Contains("\"qty\":\"2.0000\"", _server.Requests[1].Body);
        Assert.Equal(("TK01-MT-202611-000001", "DISPLAY", "STORAGE", "Staf Gudang Dev"),
            (created.Number, created.FromLocationCode, created.ToLocationCode, created.CreatedByName));
        Assert.Equal(DateTimeKind.Utc, created.TransferredAt.Kind);
        Assert.Equal("/api/v1/stock/transfers?from=2026-11-01&to=2026-11-22", _server.Requests[2].Request.RequestUri!.PathAndQuery);
        Assert.Equal(("TK01-MT-202611-000001", 1, "Asia/Jakarta"), (list[0].Number, list[0].LineCount, list[0].TimeZone));
        var detailLine = Assert.Single(detail.Lines);
        Assert.Equal(("AIR-600", "PCS", 2m), (detailLine.Sku, detailLine.UnitCode, detailLine.Qty!.Value));
        Assert.Contains("   2 PCS", TransferDetailText.Lines(detail));
    }


    [Fact]
    public async Task Shift_closing_and_close()
    {
        _server.Reply(HttpStatusCode.OK, Json("shift-closing")).Reply(HttpStatusCode.OK, Json("shift-closed"));
        var id = Guid.Parse("01a200d5-2078-71fa-a629-421d8d7f742a");

        var blind = await Client().GetShiftClosingAsync(id);
        var closed = await Client().CloseShiftAsync(new CloseShiftRequest(id, 267500m, null));

        Assert.Equal($"/api/v1/shifts/{id}/closing", _server.Requests[0].Request.RequestUri!.PathAndQuery);
        Assert.Equal(Samples.DeviceId.ToString(), _server.Requests[0].Request.Headers.GetValues(NodeApiClient.DeviceHeader).Single());
        Assert.True(blind.BlindCount);
        Assert.False(blind.HasFigures);
        Assert.Null(blind.Totals);
        Assert.Equal(("TK01-SH-202611-000001", "Test user", 200000m), (blind.Number, blind.CashierName, blind.OpeningCash));
        Assert.Contains("\"counted_cash\":\"267500.0000\"", _server.Requests[1].Body);
        Assert.True(closed.IsClosed);
        Assert.Equal((268000m, 267500m, -500m), (closed.ExpectedCash!.Value, closed.CountedCash!.Value, closed.Difference!.Value));
        Assert.Equal(["DEBIT", "QRIS", "TRANSFER", "KREDIT"], closed.NonCash!.Select(m => m.Name));
        Assert.Equal((55000m, 2), (closed.NonCash![3].Amount, closed.NonCash[3].Count));
        Assert.Equal(DateTimeKind.Utc, closed.ClosedAt!.Value.Kind);
        var report = ReceiptService.ToShiftReportData(closed);
        Assert.Equal(40000m, report.Totals.CashSales);
        Assert.Null(report.ClosedByName);                                                   // closed by the cashier
        Assert.Equal("TOKO DEV", report.HeaderLines[0]);
    }
    [Fact]
    public async Task Customer_search()
    {
        _server.Reply(HttpStatusCode.OK, Json("customers"));

        var customer = Assert.Single(await Client().SearchCustomersAsync("budi"));

        Assert.Equal("C000001", customer.Code);
        Assert.Equal(500000m, customer.CreditRemaining);
        Assert.Equal((short)14, customer.PaymentTermDays);
    }

    [Fact]
    public async Task Create_sale()
    {
        _server.Reply(HttpStatusCode.Created, Json("sale"));

        var result = await Client().CreateSaleAsync(new CreateSaleRequest(Guid.CreateVersion7(), null, [], [], null, null, null, null, null));

        Assert.StartsWith("TK01-SL-", result.Sale.Number);
        Assert.Equal(8000m, result.Sale.Total);
        Assert.Equal(2000m, result.Sale.Change);
        Assert.False(result.Replayed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Cart_cancellation()
    {
        _server.Reply(HttpStatusCode.Created, Json("cancel"));

        var result = await Client().CancelCartAsync(Guid.CreateVersion7(), "uji kontrak", 4000m, []);

        Assert.StartsWith("TK01-BT-", result.Number);
        Assert.Equal("uji kontrak", result.Reason);
        Assert.Equal(4000m, result.TotalAmount);
    }

    [Fact]
    public async Task Sale_request_uses_the_server_field_names()
    {
        _server.Reply(HttpStatusCode.Created, Json("sale"));
        var unit = Guid.CreateVersion7();

        var saleId = Guid.CreateVersion7();
        await Client().CreateSaleAsync(new CreateSaleRequest(saleId, null,
            [new SaleLineRequest(unit, 2m)], [new SalePaymentRequest("CASH", 10000m, null)], "catatan", 10m, null, "spv", "pw"));

        var body = Assert.Single(_server.Requests).Body!;
        Assert.Contains($"\"id\":\"{saleId}\"", body);
        Assert.Contains($"\"product_unit_id\":\"{unit}\"", body);
        Assert.Contains("\"qty\":\"2.0000\"", body);
        Assert.Contains("\"method\":\"CASH\"", body);
        Assert.Contains("\"amount\":\"10000.0000\"", body);
        Assert.Contains("\"discount_pct\":\"10.0000\"", body);
        Assert.Contains("\"supervisor_username\":\"spv\"", body);
        Assert.Contains("\"supervisor_password\":\"pw\"", body);
        Assert.Contains("\"notes\":\"catatan\"", body);
    }
}
