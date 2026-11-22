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
    decimal OpeningCash,
    string? CashierName = null);

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

/// <summary>A warning about a saved sale (the sale is NOT blocked).</summary>
public sealed record SaleWarningInfo(
    string Code, string Message, decimal? CreditLimit = null, decimal? OutstandingAfter = null, decimal? ExceededBy = null)
{
    private static readonly System.Globalization.CultureInfo Indonesian = System.Globalization.CultureInfo.GetCultureInfo("id-ID");

    /// <summary>What the cashier reads (Indonesian).</summary>
    public string Text => Code switch
    {
        "credit_limit_exceeded" when CreditLimit is { } limit && OutstandingAfter is { } after && ExceededBy is { } over =>
            $"Piutang pelanggan menjadi Rp {after.ToString("#,0", Indonesian)}, melebihi limit Rp {limit.ToString("#,0", Indonesian)} " +
            $"(lebih Rp {over.ToString("#,0", Indonesian)}). Penjualan tetap tersimpan.",
        "credit_limit_exceeded" => "Kredit pelanggan melebihi limit. Penjualan tetap tersimpan.",
        _ => Message,
    };
}

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

    /// <summary>A stored sale for printing (after payment and F5 reprint); always fresh from the Node Service.</summary>
    Task<SaleDetail> GetSaleAsync(Guid saleId, CancellationToken ct = default);

    Task<IReadOnlyList<StockLocation>> GetStockLocationsAsync(CancellationToken ct = default);

    /// <summary>Form Stok Total: one page of product × location rows.</summary>
    Task<StockTotalsPage> GetStockTotalsAsync(
        string? query, Guid? locationId, int offset, int limit, CancellationToken ct = default, bool includeEmpty = false);

    /// <summary>Form Cek Stok: one product's stock per expiry date.</summary>
    Task<StockExpiry> GetStockExpiryAsync(Guid productId, Guid? locationId, CancellationToken ct = default);

    /// <summary>Kartu Stok: branch dates from..to inclusive, one page of ledger rows.</summary>
    Task<StockCard> GetStockCardAsync(Guid productId, Guid? locationId, DateOnly from, DateOnly to, int offset, int limit, CancellationToken ct = default);

    /// <summary>Form Mutasi Lokasi: store the transfer (201 or the stored one for the same id).</summary>
    Task<LocationTransfer> CreateLocationTransferAsync(CreateLocationTransferRequest request, CancellationToken ct = default);

    /// <summary>FEFO preview per line from the current stock of the source location.</summary>
    Task<LocationTransferPreview> PreviewLocationTransferAsync(
        Guid fromLocationId, IReadOnlyList<LocationTransferLineRequest> lines, CancellationToken ct = default, Guid? toLocationId = null);

    /// <summary>Daftar Mutasi Lokasi: branch dates from..to inclusive, newest first.</summary>
    Task<IReadOnlyList<LocationTransferSummary>> GetLocationTransfersAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<LocationTransfer> GetLocationTransferAsync(Guid transferId, CancellationToken ct = default);
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

    /// <summary>The receipt on screen (no printer, or printing failed) with the reason.</summary>
    void ShowReceiptPreview(string message, IReadOnlyList<string> lines);

    /// <summary>"Export to File": where to save; null when cancelled.</summary>
    string? AskSaveFile(string suggestedFileName);

    /// <summary>A source document as monospace text (Kartu Stok double-click).</summary>
    /// <param name="actionText">An extra button (e.g. "Buat Mutasi Balik"); null = none.</param>
    /// <returns>True when the extra button was chosen.</returns>
    bool ShowDocumentDetail(string title, string message, IReadOnlyList<string> lines, string? actionText = null);
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

    /// <summary>
    /// Another cashier's shift is open on this PC and the user does not share it: selling is locked, the stock
    /// screens work, and a user with shift.close may close that shift.
    /// </summary>
    public ShiftInfo? ForeignShift { get; set; }

    public bool IsLoggedIn => Token is not null;

    /// <summary>The logged-in user has this permission ("*" grants all).</summary>
    public bool Has(string permission) =>
        Permissions.Contains(Pos.Shared.Auth.Permissions.All) || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public void SetLogin(LoginResult login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Token = login.AccessToken;
        TokenExpiresAt = login.ExpiresAt;
        User = login.User;
        Node = login.Node;
        Permissions = login.Permissions;
        Shift = null;
        ForeignShift = null;
    }

    public void Clear()
    {
        Token = null;
        TokenExpiresAt = null;
        User = null;
        Node = null;
        Permissions = [];
        Shift = null;
        ForeignShift = null;
    }
}

public sealed record SaleUser(Guid Id, string Username, string FullName);

public sealed record SaleCustomer(Guid Id, string Code, string Name);

public sealed record SaleDetailLine(
    short LineNo, Guid ProductId, Guid ProductUnitId, string Sku, string Name, string UnitCode,
    decimal Qty, decimal UnitPrice, decimal DiscountAmount, decimal LineTotal);

public sealed record SaleDetailPayment(
    string Method, string MethodName, bool IsCash, bool IsCredit, decimal Amount, decimal? Tendered, decimal? ChangeGiven,
    string? Reference);

/// <summary>Receipt texts per field (settings receipt.store_name, address, city, phone, footer_1, footer_2).</summary>
public sealed record ReceiptText(
    string? StoreName, string? Address, string? City, string? Phone,
    [property: System.Text.Json.Serialization.JsonPropertyName("footer_1")] string? Footer1,
    [property: System.Text.Json.Serialization.JsonPropertyName("footer_2")] string? Footer2)
{
    public IReadOnlyList<string> HeaderLines => Pos.Shared.Printing.ReceiptTexts.Header(StoreName, Address, City, Phone);

    public IReadOnlyList<string> FooterLines => Pos.Shared.Printing.ReceiptTexts.Footer(Footer1, Footer2);
}

/// <summary>GET /api/v1/sales/{id}: a stored sale with everything its receipt needs.</summary>
/// <param name="SoldAt">UTC.</param>
/// <param name="TimeZone">IANA timezone of the selling branch.</param>
public sealed record SaleDetail(
    Guid Id,
    Guid NodeId,
    string Number,
    DateTime SoldAt,
    string TimeZone,
    SaleUser Cashier,
    SaleCustomer? Customer,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal? DiscountPct,
    decimal RoundingAmount,
    decimal Total,
    decimal CreditAmount,
    decimal Change,
    DateOnly? DueDate,
    string? Notes,
    IReadOnlyList<SaleDetailLine> Lines,
    IReadOnlyList<SaleDetailPayment> Payments,
    ReceiptText Receipt);

public sealed record StockLocation(Guid Id, string Code, string Name, string Type, bool IsSellable)
{
    public string Display => $"{Code} - {Name}";
}

/// <summary>One product at one location (Form Stok Total). No purchase price or cost at a node.</summary>
public sealed record StockTotalRow(
    Guid ProductId,
    string Sku,
    string Name,
    string? Category,
    string? Brand,
    string? ShelfLocation,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    string DisplayUnitCode,
    decimal DisplayConversion,
    decimal BaseQty)
{
    /// <summary>"1 BOX 14 PCS" (same format as the cashier's Stok column).</summary>
    public string StockText => Pos.Shared.Units.StockDisplay.Format(BaseQty, DisplayUnitCode, DisplayConversion, BaseUnitCode, BaseAllowDecimal);
}

public sealed record StockTotalsPage(int Total, int Offset, int Limit, IReadOnlyList<StockTotalRow> Items);

public sealed record StockExpiryRow(Guid LocationId, string LocationCode, DateOnly? ExpiryDate, decimal BaseQty, string Status);

public sealed record StockExpiry(
    Guid ProductId,
    string Sku,
    string Name,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    string DisplayUnitCode,
    decimal DisplayConversion,
    bool TrackExpiry,
    DateOnly Today,
    int WarningDays,
    IReadOnlyList<StockExpiryRow> Rows);

/// <summary>One ledger row of the Kartu Stok (quantities in the base unit).</summary>
/// <param name="OccurredAt">UTC.</param>
public sealed record StockCardRow(
    Guid LedgerId,
    DateTime OccurredAt,
    string DocType,
    string DocLabel,
    Guid DocId,
    string? DocNumber,
    string? UserName,
    Guid LocationId,
    string LocationCode,
    DateOnly? ExpiryDate,
    decimal QtyIn,
    decimal QtyOut,
    decimal Balance);

/// <summary>GET /api/v1/stock/card: totals over the whole range, one page of rows.</summary>
public sealed record StockCard(
    Guid ProductId,
    string Sku,
    string Name,
    string BaseUnitCode,
    bool BaseAllowDecimal,
    Guid? LocationId,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    decimal OpeningBalance,
    decimal TotalIn,
    decimal TotalOut,
    decimal ClosingBalance,
    int TotalRows,
    int Offset,
    int Limit,
    IReadOnlyList<StockCardRow> Rows);

/// <summary>Node Service UTC times shown in the branch's timezone.</summary>
public static class BranchTime
{
    public static DateTime ToLocal(DateTime value, string timeZone)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
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

public sealed record LocationTransferLineRequest(Guid ProductUnitId, decimal Qty);

/// <param name="Id">UUID v7 created here; sent again after a network failure so the transfer is stored once.</param>
public sealed record CreateLocationTransferRequest(
    Guid Id, Guid FromLocationId, Guid ToLocationId, IReadOnlyList<LocationTransferLineRequest> Lines, string? Notes,
    Guid? ReversesId = null);

public sealed record ExpiryQty(DateOnly? ExpiryDate, decimal BaseQty);

/// <summary>FEFO preview of one entered line (base units): what saving would take from the source.</summary>
public sealed record LocationTransferPreviewLine(
    int LineNo, Guid ProductId, Guid ProductUnitId, decimal Qty, decimal BaseQty, string BaseUnitCode,
    decimal StockAtSource, decimal Available, decimal Shortage, IReadOnlyList<ExpiryQty> Allocations);

public sealed record LocationTransferPreview(DateOnly Today, IReadOnlyList<LocationTransferPreviewLine> Lines);

public sealed record LocationTransferLine(
    int LineNo, Guid ProductId, string Sku, string Name, Guid? ProductUnitId, string UnitCode, decimal? Qty,
    string BaseUnitCode, decimal BaseQty, IReadOnlyList<ExpiryQty> Expiries);

/// <param name="TransferredAt">UTC.</param>
public sealed record LocationTransfer(
    Guid Id, Guid NodeId, string Number, Guid FromLocationId, string FromLocationCode, Guid ToLocationId, string ToLocationCode,
    DateTime TransferredAt, string TimeZone, string? Notes, Guid CreatedBy, string CreatedByName, IReadOnlyList<LocationTransferLine> Lines,
    Guid? ReversesId = null, string? ReversesNumber = null, Guid? ReversedById = null, string? ReversedByNumber = null);

public sealed record LocationTransferSummary(
    Guid Id, string Number, DateTime TransferredAt, string FromLocationCode, string ToLocationCode, string CreatedByName,
    int LineCount, string? Notes, string TimeZone);
