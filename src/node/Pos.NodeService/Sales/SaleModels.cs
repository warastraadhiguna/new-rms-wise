namespace Pos.NodeService.Sales;

/// <summary>The cashier comes from the access token and the device from header X-Device-Id.</summary>
/// <param name="Id">UUID v7 created by the client; sending the same id again returns the stored sale.</param>
/// <param name="LocationId">Optional; defaults to the node's single sellable location.</param>
/// <param name="DiscountPct">Sale discount as a percentage, or</param>
/// <param name="DiscountAmount">sale discount as an amount (not both).</param>
/// <param name="ApprovalToken">From POST /auth/approve (action sale.discount.override), or</param>
/// <param name="SupervisorUsername">supervisor username + password, when the discount is above the cashier's limit.</param>
public sealed record CreateSaleRequest(
    Guid Id,
    Guid? CustomerId,
    Guid? LocationId,
    IReadOnlyList<SaleLineRequest>? Lines,
    IReadOnlyList<SalePaymentRequest>? Payments,
    string? Notes = null,
    decimal? DiscountPct = null,
    decimal? DiscountAmount = null,
    string? ApprovalToken = null,
    string? SupervisorUsername = null,
    string? SupervisorPassword = null);

/// <summary>Exactly one of <c>ProductUnitId</c> or <c>Barcode</c>. Discounts are manual and optional.</summary>
public sealed record SaleLineRequest(
    Guid? ProductUnitId,
    string? Barcode,
    decimal Qty,
    decimal DiscountPct = 0m,
    decimal DiscountAmount = 0m);

/// <param name="Method">Payment method code, e.g. CASH, QRIS, CREDIT.</param>
/// <param name="Amount">Money handed over with this method; for cash this may exceed the total (change).</param>
public sealed record SalePaymentRequest(string Method, decimal Amount, string? Reference = null);

/// <param name="CreditLimit">credit_limit_exceeded: the customer's limit,</param>
/// <param name="OutstandingAfter">the receivable after this sale,</param>
/// <param name="ExceededBy">and how far it is over the limit.</param>
public sealed record SaleWarning(
    string Code, string Message, decimal? CreditLimit = null, decimal? OutstandingAfter = null, decimal? ExceededBy = null);

public sealed record CreateSaleResult(SaleDto Sale, IReadOnlyList<SaleWarning> Warnings, bool Replayed);

public sealed record SaleDto(
    Guid Id,
    Guid NodeId,
    string Number,
    Guid ShiftId,
    Guid? CustomerId,
    DateTime SoldAt,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal? DiscountPct,
    Guid? DiscountApprovedBy,
    decimal TaxAmount,
    decimal RoundingAmount,
    decimal Total,
    decimal CreditAmount,
    decimal Change,
    DateOnly? DueDate,
    string? Notes,
    Guid CreatedBy,
    IReadOnlyList<SaleLineDto> Lines,
    IReadOnlyList<SalePaymentDto> Payments);

/// <param name="Stock">Expiry buckets the line was taken from (FEFO), quantities in base units.</param>
public sealed record SaleLineDto(
    Guid Id,
    int LineNo,
    Guid ProductId,
    Guid ProductUnitId,
    Guid LocationId,
    decimal Qty,
    decimal Conversion,
    decimal BaseQty,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal LineTotal,
    IReadOnlyList<SaleLineStockDto> Stock);

public sealed record SaleLineStockDto(DateOnly? ExpiryDate, decimal BaseQty);

public sealed record SalePaymentDto(
    Guid Id,
    string Method,
    decimal Amount,
    decimal? Tendered,
    decimal? ChangeGiven,
    string? Reference);

/// <summary>Points inside the sale transaction where tests can inject a failure.</summary>
public static class SaleSteps
{
    public const string StockPosted = "stock_posted";
    public const string Numbered = "numbered";
    public const string SaleInserted = "sale_inserted";
    public const string OutboxWritten = "outbox_written";
}

public sealed class SaleHooks
{
    public Func<string, CancellationToken, Task>? AfterStep { get; init; }
}
