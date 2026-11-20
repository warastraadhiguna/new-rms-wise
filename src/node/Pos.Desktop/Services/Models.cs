namespace Pos.Desktop.Services;

public sealed record UserInfo(Guid Id, string Username, string FullName);

public sealed record NodeSummary(Guid Id, string Code, string Name);

/// <summary>Response of /auth/login and /auth/pin (snake_case JSON).</summary>
public sealed record LoginResult(
    string AccessToken,
    DateTime ExpiresAt,
    UserInfo User,
    IReadOnlyList<string> Permissions,
    NodeSummary Node);

public sealed record ShiftInfo(
    Guid Id,
    string Number,
    Guid CashierId,
    string Status,
    DateTime OpenedAt,
    decimal OpeningCash);

public sealed record PriceTierInfo(decimal MinQty, decimal Price);

/// <summary>A product unit from /products/search or /products/{id}/units.</summary>
public sealed record ProductItem(
    Guid ProductId,
    Guid ProductUnitId,
    string Sku,
    string Name,
    string? Barcode,
    string UnitCode,
    string UnitName,
    decimal Conversion,
    bool AllowDecimal,
    bool AllowQtyMultiplier,
    bool TrackExpiry,
    string? Brand,
    string? Category,
    string? ShelfLocation,
    decimal? Price,
    decimal? DiscountPrice,
    IReadOnlyList<PriceTierInfo> PriceTiers,
    decimal StockBaseQty,
    decimal StockQty,
    string MatchedBy,
    string BaseUnitCode,
    bool BaseAllowDecimal)
{
    public bool IsExactMatch => MatchedBy is "barcode" or "sku";

    /// <summary>"Stok" column: plain number in the base unit, "1 BOX 14 PCS" in a larger unit.</summary>
    public string StockText => Pos.Shared.Units.StockDisplay.Format(
        StockBaseQty, UnitCode, Conversion, string.IsNullOrEmpty(BaseUnitCode) ? UnitCode : BaseUnitCode, BaseAllowDecimal);

    public Pos.Shared.Sales.CartItem ToCartItem() => new(
        ProductId, ProductUnitId, Sku, Name, UnitCode, Conversion, AllowDecimal, AllowQtyMultiplier,
        PriceTiers.Select(t => new Pos.Shared.Sales.PriceTier(t.MinQty, t.Price)).ToList(), StockBaseQty);
}

public sealed record CustomerItem(
    Guid Id,
    string Code,
    string Name,
    string? Phone,
    decimal? CreditLimit,
    decimal CreditOutstanding,
    decimal? CreditRemaining,
    short? PaymentTermDays,
    int Points)
{
    public string Display => $"{Code} - {Name}";
}

public sealed record ApprovalResult(string ApprovalToken, string Action, DateTime ExpiresAt, UserInfo ApprovedBy);

public sealed record CartCancellationLine(Guid ProductUnitId, decimal Qty, decimal UnitPrice, decimal LineTotal);

public sealed record CartCancellationResult(Guid Id, string Number, string Reason, decimal TotalAmount);

public sealed record PaymentMethodInfo(Guid Id, string Code, string Name, bool IsCash, bool IsCredit);

/// <summary>From /sales/config: what the payment dialog needs to compute the same totals as the server.</summary>
/// <param name="DiscountMaxPercent">The cashier's discount limit without a supervisor; null = no limit.</param>
public sealed record SalesConfig(
    IReadOnlyList<PaymentMethodInfo> PaymentMethods,
    decimal CashRoundingStep,
    string CashRoundingMode,
    int CurrencyDecimals,
    decimal? DiscountMaxPercent);

public sealed record SaleLineRequest(Guid ProductUnitId, decimal Qty);

public sealed record SalePaymentRequest(string Method, decimal Amount, string? Reference);

/// <param name="Id">UUID v7 created here; the same id is sent again after a network failure.</param>
public sealed record CreateSaleRequest(
    Guid Id,
    Guid? CustomerId,
    IReadOnlyList<SaleLineRequest> Lines,
    IReadOnlyList<SalePaymentRequest> Payments,
    string? Notes,
    decimal? DiscountPct,
    decimal? DiscountAmount,
    string? SupervisorUsername,
    string? SupervisorPassword);

public sealed record SaleSummary(
    Guid Id, string Number, decimal Subtotal, decimal DiscountAmount, decimal RoundingAmount, decimal Total,
    decimal Change, decimal CreditAmount);

public sealed record SaleWarningInfo(string Code, string Message);

public sealed record SaleResult(SaleSummary Sale, IReadOnlyList<SaleWarningInfo> Warnings, bool Replayed);

/// <summary>Node Service calls used by the cashier screens.</summary>
public interface INodeApi
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <returns>The device's open shift, or null when none is open.</returns>
    Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default);

    Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default);

    Task<IReadOnlyList<ProductItem>> SearchProductsAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<ProductItem>> GetProductUnitsAsync(Guid productId, CancellationToken ct = default);

    Task<IReadOnlyList<CustomerItem>> SearchCustomersAsync(string query, CancellationToken ct = default);

    /// <summary>Supervisor username + password approve one action for the logged-in user.</summary>
    Task<ApprovalResult> ApproveAsync(string action, string username, string password, CancellationToken ct = default);

    Task<CartCancellationResult> CancelCartAsync(
        Guid id, string reason, decimal totalAmount, IReadOnlyList<CartCancellationLine> lines, CancellationToken ct = default);

    Task<SalesConfig> GetSalesConfigAsync(CancellationToken ct = default);

    Task<SaleResult> CreateSaleAsync(CreateSaleRequest request, CancellationToken ct = default);
}

public sealed record SupervisorCredentials(string Username, string Password);

/// <summary>Modal dialogs used by the sales screen (fakes in tests, WPF windows in the app).</summary>
public interface IDialogService
{
    /// <returns>The new quantity, or null when cancelled.</returns>
    decimal? AskQuantity(string productName, decimal current);

    /// <returns>Supervisor username + password, or null when cancelled.</returns>
    SupervisorCredentials? AskSupervisor(string purpose);

    /// <returns>The reason (never blank), or null when cancelled.</returns>
    string? AskCancelReason();

    bool Confirm(string message);

    /// <summary>F1: large product search; returns the chosen unit or null.</summary>
    ProductItem? PickProduct(string initialQuery);

    ProductItem? PickUnit(string productName, IReadOnlyList<ProductItem> units, Guid currentUnitId);

    CustomerItem? PickCustomer(string initialQuery);

    /// <summary>F6 "Bayar": the payment dialog; returns the saved sale, or null when closed without saving.</summary>
    SaleResult? ShowPayment(ViewModels.PaymentViewModel payment);

    /// <summary>After a successful sale: nota number and change, shown large.</summary>
    void ShowSaleResult(SaleResult result);
}

/// <summary>Switches between the top-level windows.</summary>
public interface INavigator
{
    void ShowLogin();

    void ShowOpenShift();

    void ShowMain();
}

/// <summary>The logged-in user, node and shift of this PC. One per application.</summary>
public sealed class SessionState
{
    public string? Token { get; private set; }
    public DateTime? TokenExpiresAt { get; private set; }
    public UserInfo? User { get; private set; }
    public NodeSummary? Node { get; private set; }
    public IReadOnlyList<string> Permissions { get; private set; } = [];
    public ShiftInfo? Shift { get; set; }

    public bool IsLoggedIn => Token is not null;

    public void SetLogin(LoginResult login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Token = login.AccessToken;
        TokenExpiresAt = login.ExpiresAt;
        User = login.User;
        Node = login.Node;
        Permissions = login.Permissions;
        Shift = null;
    }

    public void Clear()
    {
        Token = null;
        TokenExpiresAt = null;
        User = null;
        Node = null;
        Permissions = [];
        Shift = null;
    }
}
