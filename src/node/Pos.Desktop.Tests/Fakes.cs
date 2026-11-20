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
        decimal conversion = 1m, bool allowMultiplier = true, decimal stock = 100m, string? barcode = null, Guid? unitId = null) =>
        new(productId, unitId ?? Guid.CreateVersion7(), sku, name, barcode ?? "899" + sku, unit, unit, conversion,
            AllowDecimal: false, AllowQtyMultiplier: allowMultiplier, TrackExpiry: false, "Merek X", "Jenis Y", "Rak A",
            price, null, [new PriceTierInfo(1m, price)], stock, stock / conversion, "name");

    public static readonly ProductItem Amplop = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 7500m);
    public static readonly ProductItem AmplopPak = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 70000m, unit: "PAK", conversion: 10m);
    public static readonly ProductItem Kantong = Product(KantongId, "5113", "2_HD kantong hitam 3kg", 4500m, unit: "BUNGKUS");
    public static readonly ProductItem Voucher = Product(VoucherId, "VCR10", "Voucher pulsa 10rb", 10500m, allowMultiplier: false);

    public static LoginResult LoginWith(params string[] permissions) => Login() with { Permissions = permissions };
}
