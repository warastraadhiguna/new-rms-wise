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
