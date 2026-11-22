using Dapper;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Sales;
using Pos.Shared.Time;

namespace Pos.NodeService.Sales;

/// <summary>One row of Data Penjualan (RMS 2 columns). Times UTC; <see cref="SalesListPage.TimeZone"/> is the branch's.</summary>
/// <param name="Total">Before the sale discount (RMS 2 "Total" = subtotal).</param>
/// <param name="GrandTotal">After discount and rounding.</param>
/// <param name="Outstanding">"Total Tertanggung": credit still owed (0 when voided).</param>
/// <param name="Status">PAID, UNPAID, RETURNED or VOID (<see cref="SaleStatuses"/>).</param>
/// <param name="ShiftOpen">The sale's shift is still open (a void is only possible then).</param>
public sealed record SalesListRow(
    Guid Id,
    string Number,
    DateTime SoldAt,
    string? CustomerCode,
    string? CustomerName,
    string CashierName,
    string PaymentMethods,
    decimal Total,
    decimal? DiscountPct,
    decimal DiscountAmount,
    decimal GrandTotal,
    decimal CreditAmount,
    decimal Outstanding,
    string Status,
    DateOnly? DueDate,
    string? Notes,
    bool ShiftOpen);

/// <param name="EarliestDate">The first date this user may see (null = no limit).</param>
public sealed record SalesListPage(IReadOnlyList<SalesListRow> Items, int Total, string TimeZone, DateOnly? EarliestDate);

/// <summary>
/// "Data Penjualan": this node's sales in branch dates <c>from</c>..<c>to</c>, newest first, searched by nota number,
/// member (code or name) or cashier (name or username). Without <c>sale.history</c> a user sees only the last
/// <c>sales.cashier_history_days</c> days (default 15, RMS 2 "H-15").
/// </summary>
public sealed class SalesListService(DbConnectionFactory connections, NodeIdentity nodeIdentity, TimeProvider clock)
{
    public const string HistoryDaysSetting = "sales.cashier_history_days";
    public const int DefaultHistoryDays = 15;
    public const int MaxPageSize = 1000;

    public async Task<SalesListPage> ListAsync(
        DateOnly from, DateOnly to, string? q, int offset, int limit, bool unlimitedHistory, CancellationToken ct = default)
    {
        if (to < from)
            throw ApiException.Unprocessable("invalid_range", "The end date is before the start date.");
        if (offset < 0 || limit is < 1 or > MaxPageSize)
            throw ApiException.Unprocessable("invalid_paging", $"offset must be >= 0 and limit 1..{MaxPageSize}.");

        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        DateOnly? earliest = null;
        if (!unlimitedHistory)
        {
            var days = await SettingsReader.GetIntAsync(c, null, HistoryDaysSetting, node.BranchId, DefaultHistoryDays, ct);
            earliest = BranchClock.Today(clock.GetUtcNow().UtcDateTime, node.TimeZone).AddDays(-Math.Max(0, days));
            if (from < earliest)
                throw new ApiException(StatusCodes.Status403Forbidden, "history_limit",
                    $"Without permission sale.history the earliest date is {earliest:yyyy-MM-dd}.",
                    new Dictionary<string, object?> { ["earliest_date"] = earliest.Value.ToString("yyyy-MM-dd") });
        }

        var (fromUtc, toUtc) = BranchClock.DayRangeUtc(from, to, TimeZoneInfo.FindSystemTimeZoneById(node.TimeZone));
        var text = (q ?? "").Trim();
        var args = new { nodeId = node.Id, fromUtc, toUtc, text, like = "%" + EscapeLike(text) + "%", offset, limit };
        const string Where =
            """
            WHERE s.node_id = @nodeId AND s.sold_at >= @fromUtc AND s.sold_at < @toUtc
              AND (@text = '' OR s.number LIKE @like OR ct.code LIKE @like OR ct.name LIKE @like
                   OR u.full_name LIKE @like OR u.username LIKE @like)
            """;

        var total = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT COUNT(*) FROM sales s JOIN users u ON u.id = s.created_by LEFT JOIN contacts ct ON ct.id = s.customer_id
            {Where}
            """, args, cancellationToken: ct));

        var rows = await c.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT s.id AS Id, s.number AS Number, s.sold_at AS SoldAt, ct.code AS CustomerCode, ct.name AS CustomerName,
                   u.full_name AS CashierName,
                   COALESCE((SELECT GROUP_CONCAT(DISTINCT pm.name ORDER BY pm.is_credit, pm.is_cash DESC, pm.name SEPARATOR ' + ')
                             FROM sale_payments sp JOIN payment_methods pm ON pm.id = sp.payment_method_id
                             WHERE sp.sale_id = s.id), '') AS PaymentMethods,
                   s.subtotal AS Total, s.discount_pct AS DiscountPct, s.discount_amount AS DiscountAmount, s.total AS GrandTotal,
                   s.credit_amount AS CreditAmount, s.due_date AS DueDate, s.notes AS Notes,
                   (v.id IS NOT NULL) AS Voided,
                   (SELECT COUNT(*) FROM sale_returns r WHERE r.sale_id = s.id) AS ReturnCount,
                   COALESCE((SELECT SUM(a.amount) FROM receivable_payment_allocations a WHERE a.sale_id = s.id), 0) AS Paid,
                   COALESCE((SELECT SUM(r.total) FROM sale_returns r WHERE r.sale_id = s.id AND r.settlement = 'REDUCE_RECEIVABLE'), 0) AS Reduced,
                   (cs.status = 'OPEN') AS ShiftOpen
            FROM sales s
            JOIN users u ON u.id = s.created_by
            JOIN cashier_shifts cs ON cs.id = s.shift_id
            LEFT JOIN contacts ct ON ct.id = s.customer_id
            LEFT JOIN sale_voids v ON v.sale_id = s.id
            {Where}
            ORDER BY s.sold_at DESC, s.number DESC
            LIMIT @limit OFFSET @offset
            """, args, cancellationToken: ct));

        var items = rows.Select(r =>
        {
            var voided = r.Voided != 0;
            var outstanding = SaleBalance.Outstanding(r.CreditAmount, r.Paid, r.Reduced, voided);
            return new SalesListRow(r.Id, r.Number, DateTime.SpecifyKind(r.SoldAt, DateTimeKind.Utc), r.CustomerCode, r.CustomerName,
                r.CashierName, r.PaymentMethods, r.Total, r.DiscountPct, r.DiscountAmount, r.GrandTotal, r.CreditAmount, outstanding,
                SaleBalance.Status(voided, outstanding, r.ReturnCount > 0),
                r.DueDate is { } due ? DateOnly.FromDateTime(due) : null, r.Notes, r.ShiftOpen != 0);
        }).ToList();
        return new SalesListPage(items, (int)total, node.TimeZone, earliest);
    }

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
        public DateTime SoldAt { get; set; }
        public string? CustomerCode { get; set; }
        public string? CustomerName { get; set; }
        public string CashierName { get; set; } = "";
        public string PaymentMethods { get; set; } = "";
        public decimal Total { get; set; }
        public decimal? DiscountPct { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal CreditAmount { get; set; }
        public DateTime? DueDate { get; set; }
        public string? Notes { get; set; }
        public long Voided { get; set; }
        public long ReturnCount { get; set; }
        public decimal Paid { get; set; }
        public decimal Reduced { get; set; }
        public long ShiftOpen { get; set; }
    }
}
