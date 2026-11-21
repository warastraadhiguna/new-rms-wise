using System.Net;
using System.Text;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

/// <summary>A fake Node Service: records requests and answers with canned responses.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeServer Reply(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, status >= HttpStatusCode.BadRequest ? "application/problem+json" : "application/json"),
        });
        return this;
    }

    public FakeServer Problem(HttpStatusCode status, string code, string extra = "") =>
        Reply(status, $$"""{"title":"{{code}}","status":{{(int)status}},"detail":"server detail","code":"{{code}}"{{extra}}}""");

    public FakeServer Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        if (_responses.Count == 0)
            throw new InvalidOperationException($"No response prepared for {request.Method} {request.RequestUri}");
        return _responses.Dequeue()(request);
    }
}

/// <summary>Scriptable INodeApi for ViewModel tests.</summary>
public sealed class FakeNodeApi : INodeApi
{
    public Func<string, string, LoginResult>? OnLogin { get; set; }
    public Func<ShiftInfo?>? OnCurrentShift { get; set; }
    public Func<decimal, ShiftInfo>? OnOpenShift { get; set; }

    public List<string> Calls { get; } = [];

    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        Calls.Add($"login:{username}");
        return Task.FromResult(OnLogin!(username, password));
    }

    public Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default)
    {
        Calls.Add("current-shift");
        return Task.FromResult(OnCurrentShift!());
    }

    public Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default)
    {
        Calls.Add($"open-shift:{openingCash}");
        return Task.FromResult(OnOpenShift!(openingCash));
    }

    /// <summary>Products returned by search: by default every product whose code, barcode or name contains the query.</summary>
    public List<ProductItem> Catalog { get; } = [];
    public Func<Guid, IReadOnlyList<ProductItem>>? OnUnits { get; set; }
    public List<CustomerItem> Customers { get; } = [];
    public Func<string, string, string, ApprovalResult>? OnApprove { get; set; }
    public Func<Guid, string, decimal, IReadOnlyList<CartCancellationLine>, CartCancellationResult>? OnCancelCart { get; set; }

    public Task<IReadOnlyList<ProductItem>> SearchProductsAsync(string query, CancellationToken ct = default)
    {
        Calls.Add($"search:{query}");
        IReadOnlyList<ProductItem> result = Catalog
            .Select(p => p.Barcode == query ? p with { MatchedBy = "barcode" }
                : p.Sku == query ? p with { MatchedBy = "sku" }
                : p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? p with { MatchedBy = "name" }
                : null)
            .Where(p => p is not null)
            .OrderBy(p => p!.MatchedBy == "name" ? 1 : 0)
            .ToList()!;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProductItem>> GetProductUnitsAsync(Guid productId, CancellationToken ct = default)
    {
        Calls.Add($"units:{productId}");
        return Task.FromResult(OnUnits!(productId));
    }

    public Task<IReadOnlyList<CustomerItem>> SearchCustomersAsync(string query, CancellationToken ct = default)
    {
        Calls.Add($"customers:{query}");
        IReadOnlyList<CustomerItem> result = Customers
            .Where(c => c.Code == query || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(result);
    }

    public Task<ApprovalResult> ApproveAsync(string action, string username, string password, CancellationToken ct = default)
    {
        Calls.Add($"approve:{action}:{username}");
        return Task.FromResult(OnApprove!(action, username, password));
    }

    public Task<CartCancellationResult> CancelCartAsync(
        Guid id, string reason, decimal totalAmount, IReadOnlyList<CartCancellationLine> lines, CancellationToken ct = default)
    {
        Calls.Add($"cancel-cart:{reason}");
        return Task.FromResult(OnCancelCart!(id, reason, totalAmount, lines));
    }

    public SalesConfig Config { get; set; } = Samples.Config();
    public Func<CreateSaleRequest, SaleResult>? OnCreateSale { get; set; }
    public List<CreateSaleRequest> SaleRequests { get; } = [];

    public Task<SalesConfig> GetSalesConfigAsync(CancellationToken ct = default)
    {
        Calls.Add("sales-config");
        return Task.FromResult(Config);
    }

    public Task<SaleResult> CreateSaleAsync(CreateSaleRequest request, CancellationToken ct = default)
    {
        Calls.Add("create-sale");
        SaleRequests.Add(request);
        return Task.FromResult(OnCreateSale!(request));
    }

    /// <summary>GET /sales/{id}; default: a cash sale with that id. Throw from it to simulate a failure.</summary>
    public Func<Guid, SaleDetail>? OnGetSale { get; set; }

    public Task<SaleDetail> GetSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        Calls.Add($"get-sale:{saleId}");
        return Task.FromResult((OnGetSale ?? (id => Receipts.Detail(id)))(saleId));
    }

    // ---- stock screens ---------------------------------------------------------------------------

    public List<StockLocation> StockLocations { get; } = [Stock.Display, Stock.Storage];

    /// <summary>Rows served by /stock/totals: filtered by SKU/name and location, then paged like the server.</summary>
    public List<StockTotalRow> StockRows { get; } = [];

    public Func<Guid, Guid?, StockExpiry>? OnStockExpiry { get; set; }

    public Task<IReadOnlyList<StockLocation>> GetStockLocationsAsync(CancellationToken ct = default)
    {
        Calls.Add("stock-locations");
        return Task.FromResult<IReadOnlyList<StockLocation>>(StockLocations);
    }

    public Task<StockTotalsPage> GetStockTotalsAsync(string? query, Guid? locationId, int offset, int limit, CancellationToken ct = default)
    {
        Calls.Add($"stock-totals:{query}:{locationId?.ToString() ?? "all"}:{offset}:{limit}");
        var q = (query ?? "").Trim();
        var matching = StockRows
            .Where(r => locationId is null || r.LocationId == locationId)
            .Where(r => q.Length == 0 || r.Sku == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(new StockTotalsPage(matching.Count, offset, limit, matching.Skip(offset).Take(limit).ToList()));
    }

    public Task<StockExpiry> GetStockExpiryAsync(Guid productId, Guid? locationId, CancellationToken ct = default)
    {
        Calls.Add($"stock-expiry:{productId}:{locationId?.ToString() ?? "all"}");
        return Task.FromResult((OnStockExpiry ?? ((id, location) => Stock.Expiry(id, location)))(productId, locationId));
    }

    /// <summary>The whole card (all rows); each call returns the requested page of it, like the server.</summary>
    public StockCard StockCardData { get; set; } = Stock.Card(3);

    public Task<StockCard> GetStockCardAsync(Guid productId, Guid? locationId, DateOnly from, DateOnly to, int offset, int limit, CancellationToken ct = default)
    {
        Calls.Add($"stock-card:{productId}:{locationId?.ToString() ?? "all"}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{offset}:{limit}");
        var card = StockCardData;
        return Task.FromResult(card with
        {
            ProductId = productId,
            LocationId = locationId,
            From = from,
            To = to,
            Offset = offset,
            Limit = limit,
            Rows = card.Rows.Skip(offset).Take(limit).ToList(),
        });
    }
}

/// <summary>Scripted answers for the sales screen's dialogs; records what was asked.</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<string> Asked { get; } = [];
    public decimal? Quantity { get; set; }
    public SupervisorCredentials? Supervisor { get; set; }
    public string? CancelReason { get; set; }
    public bool ConfirmAnswer { get; set; }
    public ProductItem? ProductToPick { get; set; }
    public Func<IReadOnlyList<ProductItem>, ProductItem?>? UnitToPick { get; set; }
    public CustomerItem? CustomerToPick { get; set; }

    public decimal? AskQuantity(string productName, decimal current) { Asked.Add($"qty:{productName}:{current}"); return Quantity; }
    public SupervisorCredentials? AskSupervisor(string purpose) { Asked.Add($"supervisor:{purpose}"); return Supervisor; }
    public string? AskCancelReason() { Asked.Add("cancel-reason"); return CancelReason; }
    public bool Confirm(string message) { Asked.Add($"confirm:{message}"); return ConfirmAnswer; }
    public ProductItem? PickProduct(string initialQuery) { Asked.Add($"pick-product:{initialQuery}"); return ProductToPick; }

    public ProductItem? PickUnit(string productName, IReadOnlyList<ProductItem> units, Guid currentUnitId)
    {
        Asked.Add($"pick-unit:{productName}");
        return UnitToPick?.Invoke(units);
    }

    public CustomerItem? PickCustomer(string initialQuery) { Asked.Add($"pick-customer:{initialQuery}"); return CustomerToPick; }

    /// <summary>Plays the cashier in the payment dialog: gets the dialog's ViewModel, returns what the dialog would.</summary>
    public Func<ViewModels.PaymentViewModel, SaleResult?>? OnPayment { get; set; }
    public List<ViewModels.PaymentViewModel> Payments { get; } = [];
    public List<SaleResult> ShownResults { get; } = [];

    public SaleResult? ShowPayment(ViewModels.PaymentViewModel payment)
    {
        Asked.Add("payment");
        Payments.Add(payment);
        return OnPayment?.Invoke(payment);
    }

    public void ShowSaleResult(SaleResult result) { Asked.Add("sale-result"); ShownResults.Add(result); }

    public List<(string Message, IReadOnlyList<string> Lines)> Previews { get; } = [];

    public void ShowReceiptPreview(string message, IReadOnlyList<string> lines) { Asked.Add("receipt-preview"); Previews.Add((message, lines)); }

    /// <summary>Path returned by "Export to File"; null = the cashier cancelled the save dialog.</summary>
    public string? SaveFilePath { get; set; }

    public string? AskSaveFile(string suggestedFileName) { Asked.Add($"save-file:{suggestedFileName}"); return SaveFilePath; }

    public List<(string Title, string Message, IReadOnlyList<string> Lines)> Documents { get; } = [];

    public void ShowDocumentDetail(string title, string message, IReadOnlyList<string> lines) { Asked.Add($"document:{title}"); Documents.Add((title, message, lines)); }
}

public sealed class MemoryCartStore : ICartStore
{
    public CartSnapshot? Saved { get; set; }
    public int Saves { get; private set; }

    public CartSnapshot? Load() => Saved;
    public void Save(CartSnapshot snapshot) { Saved = snapshot; Saves++; }
    public void Clear() => Saved = null;
}

public sealed class MemoryViewSettingsStore : IViewSettingsStore
{
    public SalesViewSettings Settings { get; set; } = new();
    public SalesViewSettings Load() => Settings;
    public void Save(SalesViewSettings settings) => Settings = settings;
}

public sealed class FakeNavigator : INavigator
{
    public List<string> Shown { get; } = [];

    public void ShowLogin() => Shown.Add("login");
    public void ShowOpenShift() => Shown.Add("open-shift");
    public void ShowMain() => Shown.Add("main");
}

public static class Samples
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");
    public static readonly Guid OtherUserId = Guid.Parse("01930000-0000-7000-8000-000000000002");
    public static readonly Guid DeviceId = Guid.Parse("01930000-0000-7000-8000-0000000000d1");

    public static LoginResult Login(Guid? userId = null) => new(
        "jwt-token", new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc),
        new UserInfo(userId ?? UserId, "kasir1", "Kasir Satu"),
        ["sale.create", "shift.open"],
        new NodeSummary(Guid.CreateVersion7(), "TK01", "Toko Dev"));

    public static ShiftInfo Shift(Guid? cashierId = null) => new(
        Guid.CreateVersion7(), "TK01-SH-202611-000007", cashierId ?? UserId, "OPEN",
        new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc), 200000m);

    public static readonly Guid AmplopId = Guid.Parse("01930000-0000-7000-8000-00000000a001");
    public static readonly Guid KantongId = Guid.Parse("01930000-0000-7000-8000-00000000b001");
    public static readonly Guid VoucherId = Guid.Parse("01930000-0000-7000-8000-00000000c001");

    public static ProductItem Product(Guid productId, string sku, string name, decimal price, string unit = "PCS",
        decimal conversion = 1m, bool allowMultiplier = true, decimal stock = 100m, string? barcode = null, Guid? unitId = null,
        string? baseUnit = null) =>
        new(productId, unitId ?? Guid.CreateVersion7(), sku, name, barcode ?? "899" + sku, unit, unit, conversion,
            AllowDecimal: false, AllowQtyMultiplier: allowMultiplier, TrackExpiry: false, "Merek X", "Jenis Y", "Rak A",
            price, null, [new PriceTierInfo(1m, price)], stock, stock / conversion, "name",
            baseUnit ?? (conversion == 1m ? unit : "PCS"), BaseAllowDecimal: false);

    public static readonly ProductItem Amplop = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 7500m);
    public static readonly ProductItem AmplopPak = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 70000m, unit: "PAK", conversion: 10m);
    public static readonly ProductItem Kantong = Product(KantongId, "5113", "2_HD kantong hitam 3kg", 4500m, unit: "BUNGKUS");
    public static readonly ProductItem Voucher = Product(VoucherId, "VCR10", "Voucher pulsa 10rb", 10500m, allowMultiplier: false);

    public static LoginResult LoginWith(params string[] permissions) => Login() with { Permissions = permissions };

    public static SalesConfig Config(decimal? discountMax = 10m, decimal step = 100m) => new(
        [
            new PaymentMethodInfo(Guid.CreateVersion7(), "CASH", "TUNAI", IsCash: true, IsCredit: false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "DEBIT", "DEBIT", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "QRIS", "QRIS", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "TRANSFER", "TRANSFER", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "CREDIT", "KREDIT", false, IsCredit: true),
        ],
        step, "nearest", 0, discountMax);

    /// <summary>What the server would answer for a request (totals are not recomputed here).</summary>
    public static SaleResult Saved(CreateSaleRequest request, string number, decimal total, decimal change, params SaleWarningInfo[] warnings) =>
        new(new SaleSummary(request.Id, number, total, 0m, 0m, total, change, 0m), warnings, Replayed: false);
}

/// <summary>Records RAW print jobs; <see cref="Failure"/> makes the next jobs fail like an offline printer.</summary>
public sealed class FakePrinter : IReceiptPrinter
{
    public List<(string Printer, byte[] Data, string Document)> Jobs { get; } = [];
    public string? Failure { get; set; }

    public void PrintRaw(string printerName, byte[] data, string documentName)
    {
        if (Failure is not null)
            throw new PrinterException(Failure);
        Jobs.Add((printerName, data, documentName));
    }
}

public sealed class MemoryLastSaleStore : ILastSaleStore
{
    public Guid? SaleId { get; set; }
    public Guid? Load() => SaleId;
    public void Save(Guid saleId) => SaleId = saleId;
}

public static class Receipts
{
    public static ReceiptService Service(
        INodeApi api, IDialogService dialogs, FakePrinter? printer = null, string? printerName = "EPSON TM-T82II Receipt",
        int width = 48, ILastSaleStore? lastSale = null) =>
        new(api, printer ?? new FakePrinter(), dialogs, new ReceiptPrinterSettings(printerName, width), lastSale ?? new MemoryLastSaleStore());

    /// <summary>A stored cash sale as GET /api/v1/sales/{id} returns it (sold 11:05 UTC = 18:05 WIB).</summary>
    public static SaleDetail Detail(Guid id, string number = "TK01-SL-202611-000123", bool cash = true) => new(
        id, Guid.CreateVersion7(), number, new DateTime(2026, 11, 20, 11, 5, 0, DateTimeKind.Utc), "Asia/Jakarta",
        new SaleUser(Samples.UserId, "kasir1", "Kasir Satu"), null,
        19500m, 0m, null, 0m, 19500m, 0m, cash ? 500m : 0m, null, null,
        [
            new SaleDetailLine(1, Samples.AmplopId, Guid.CreateVersion7(), "13589", "2023_amplop lebaran karakter", "PCS", 2m, 7500m, 0m, 15000m),
            new SaleDetailLine(2, Samples.KantongId, Guid.CreateVersion7(), "5113", "2_HD kantong hitam 3kg", "BUNGKUS", 1m, 4500m, 0m, 4500m),
        ],
        [cash
            ? new SaleDetailPayment("CASH", "TUNAI", true, false, 19500m, 20000m, 500m, null)
            : new SaleDetailPayment("QRIS", "QRIS", false, false, 19500m, null, null, "REF1")],
        new ReceiptText("TOKO DEV", "Jl. Merdeka No. 1", null, null, "Terimakasih Atas Kunjungannya", null));
}

public sealed class MemoryPreferenceStore<T>(T initial) : IPreferenceStore<T> where T : class
{
    public T Value { get; set; } = initial;
    public T Load() => Value;
    public void Save(T value) => Value = value;
}

/// <summary>Sample stock data for the admin screens.</summary>
public static class Stock
{
    public static readonly StockLocation Display = new(Guid.Parse("01930000-0000-7000-8000-0000000000a1"), "DISPLAY", "Display", "DISPLAY", true);
    public static readonly StockLocation Storage = new(Guid.Parse("01930000-0000-7000-8000-0000000000a2"), "STORAGE", "Gudang toko", "STORAGE", false);

    public static StockTotalRow Row(string sku, string name, decimal baseQty, StockLocation? location = null, string displayUnit = "BOX",
        decimal conversion = 24m, Guid? productId = null) =>
        new(productId ?? Guid.CreateVersion7(), sku, name, "Minuman", "Merek X", "Rak A", (location ?? Display).Id, (location ?? Display).Code,
            (location ?? Display).Name, "PCS", false, displayUnit, conversion, baseQty);

    public static readonly Guid SaleDocId = Guid.Parse("01930000-0000-7000-8000-0000000000e1");

    /// <summary>
    /// A card with opening 10 and <paramref name="rows"/> movements: SALE -2 (first row, doc <see cref="SaleDocId"/>),
    /// then alternating ADJUSTMENT +5 / SALE -2, one minute apart from 20 Nov 2026 02:00 UTC (09:00 WIB).
    /// </summary>
    public static StockCard Card(int rows)
    {
        var list = new List<StockCardRow>();
        var balance = 10m;
        for (var i = 0; i < rows; i++)
        {
            var sale = i % 2 == 0;
            var qty = sale ? -2m : 5m;
            balance += qty;
            list.Add(new StockCardRow(Guid.CreateVersion7(), new DateTime(2026, 11, 20, 2, i % 60, 0, DateTimeKind.Utc).AddHours(i / 60),
                sale ? "SALE" : "ADJUSTMENT", sale ? "Penjualan" : "Penyesuaian", i == 0 ? SaleDocId : Guid.CreateVersion7(),
                sale ? $"TK01-SL-202611-{i + 1:000000}" : $"TK01-ADJ-202611-{i + 1:000000}", sale ? "Kasir Satu" : "Admin Dev",
                Display.Id, "DISPLAY", sale ? new DateOnly(2027, 6, 1) : null, Math.Max(qty, 0), Math.Max(-qty, 0), balance));
        }
        return new StockCard(Guid.Empty, "AIR-600", "Air mineral 600 ml", "PCS", false, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22),
            "Asia/Jakarta", 10m, list.Sum(r => r.QtyIn), list.Sum(r => r.QtyOut), balance, rows, 0, rows, list);
    }

    public static StockExpiry Expiry(Guid productId, Guid? locationId) => new(
        productId, "AIR-600", "Air mineral 600 ml", "PCS", false, "BOX", 24m, true, new DateOnly(2026, 11, 22), 30,
        [
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2026, 11, 19), 2m, "expired"),
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2026, 12, 2), 30m, "near_expiry"),
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2027, 6, 1), 48m, "ok"),
        ]);
}

