using System.Text.Json;
using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.Shared.Printing;

namespace Pos.NodeService.Sales;

public sealed record SaleUserDto(Guid Id, string Username, string FullName);

public sealed record SaleCustomerDto(Guid Id, string Code, string Name);

public sealed record SaleDetailLineDto(
    short LineNo, Guid ProductId, Guid ProductUnitId, string Sku, string Name, string UnitCode,
    decimal Qty, decimal UnitPrice, decimal DiscountAmount, decimal LineTotal);

/// <param name="Method">Payment method code (CASH, CREDIT, ...).</param>
/// <param name="MethodName">As printed (TUNAI, KREDIT, ...).</param>
public sealed record SaleDetailPaymentDto(
    string Method, string MethodName, bool IsCash, bool IsCredit, decimal Amount, decimal? Tendered, decimal? ChangeGiven,
    string? Reference);

/// <summary>Settings <c>receipt.header_lines</c> / <c>receipt.footer_lines</c> of the sale's branch.</summary>
public sealed record ReceiptTextDto(IReadOnlyList<string> HeaderLines, IReadOnlyList<string> FooterLines);

/// <summary>A stored sale with what a receipt needs (names, cashier, branch timezone, receipt texts).</summary>
/// <param name="SoldAt">UTC.</param>
/// <param name="TimeZone">IANA timezone of the branch that made the sale (for the printed date).</param>
public sealed record SaleDetailDto(
    Guid Id,
    Guid NodeId,
    string Number,
    DateTime SoldAt,
    string TimeZone,
    SaleUserDto Cashier,
    SaleCustomerDto? Customer,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal? DiscountPct,
    decimal RoundingAmount,
    decimal Total,
    decimal CreditAmount,
    decimal Change,
    DateOnly? DueDate,
    string? Notes,
    IReadOnlyList<SaleDetailLineDto> Lines,
    IReadOnlyList<SaleDetailPaymentDto> Payments,
    ReceiptTextDto Receipt);

/// <summary>GET /api/v1/sales/{id}: read model for receipts (also replicas of other nodes' sales).</summary>
public sealed class SaleDetailService(DbConnectionFactory connections)
{
    public const string HeaderLinesKey = "receipt.header_lines";
    public const string FooterLinesKey = "receipt.footer_lines";

    public async Task<SaleDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await connections.OpenAsync(ct);
        var h = await c.QuerySingleOrDefaultAsync<HeaderRow>(new CommandDefinition(
            """
            SELECT s.id AS Id, s.node_id AS NodeId, s.number AS Number, s.sold_at AS SoldAt, b.timezone AS TimeZone,
                   b.id AS BranchId, n.name AS NodeName,
                   u.id AS CashierId, u.username AS CashierUsername, u.full_name AS CashierName,
                   ct.id AS CustomerId, ct.code AS CustomerCode, ct.name AS CustomerName,
                   s.subtotal AS Subtotal, s.discount_amount AS DiscountAmount, s.discount_pct AS DiscountPct,
                   s.rounding_amount AS RoundingAmount, s.total AS Total, s.credit_amount AS CreditAmount,
                   s.due_date AS DueDate, s.notes AS Notes
            FROM sales s
            JOIN nodes n ON n.id = s.node_id
            JOIN branches b ON b.id = n.branch_id
            JOIN users u ON u.id = s.created_by
            LEFT JOIN contacts ct ON ct.id = s.customer_id
            WHERE s.id = @id
            """, new { id }, cancellationToken: ct))
            ?? throw ApiException.NotFound("unknown_sale", $"Sale {id} does not exist at this node.");

        var lines = (await c.QueryAsync<SaleDetailLineDto>(new CommandDefinition(
            """
            SELECT sl.line_no AS LineNo, sl.product_id AS ProductId, sl.product_unit_id AS ProductUnitId, p.sku AS Sku,
                   p.name AS Name, un.code AS UnitCode, sl.qty AS Qty, sl.unit_price AS UnitPrice,
                   sl.discount_amount AS DiscountAmount, sl.line_total AS LineTotal
            FROM sale_lines sl
            JOIN products p ON p.id = sl.product_id
            JOIN product_units pu ON pu.id = sl.product_unit_id
            JOIN units un ON un.id = pu.unit_id
            WHERE sl.sale_id = @id ORDER BY sl.line_no
            """, new { id }, cancellationToken: ct))).ToList();

        var payments = (await c.QueryAsync<SaleDetailPaymentDto>(new CommandDefinition(
            """
            SELECT pm.code AS Method, pm.name AS MethodName, pm.is_cash AS IsCash, pm.is_credit AS IsCredit,
                   sp.amount AS Amount, sp.tendered AS Tendered, sp.change_given AS ChangeGiven, sp.reference AS Reference
            FROM sale_payments sp JOIN payment_methods pm ON pm.id = sp.payment_method_id
            WHERE sp.sale_id = @id ORDER BY pm.is_cash, sp.id
            """, new { id }, cancellationToken: ct))).ToList();

        var receipt = await LoadReceiptTextAsync(c, h.BranchId, h.NodeName, ct);

        return new SaleDetailDto(
            h.Id, h.NodeId, h.Number, DateTime.SpecifyKind(h.SoldAt, DateTimeKind.Utc), h.TimeZone,
            new SaleUserDto(h.CashierId, h.CashierUsername, h.CashierName),
            h.CustomerId is { } customerId ? new SaleCustomerDto(customerId, h.CustomerCode!, h.CustomerName!) : null,
            h.Subtotal, h.DiscountAmount, h.DiscountPct, h.RoundingAmount, h.Total, h.CreditAmount,
            payments.Sum(p => p.ChangeGiven ?? 0m),
            h.DueDate is { } due ? DateOnly.FromDateTime(due) : null,
            h.Notes, lines, payments, receipt);
    }

    /// <summary>
    /// Branch row wins over the global row. Missing header → the store name; missing footer → the RMS 2 thank-you
    /// line. An empty array means "print nothing there".
    /// </summary>
    private static async Task<ReceiptTextDto> LoadReceiptTextAsync(MySqlConnection c, Guid branchId, string nodeName, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            """
            SELECT setting_key, CAST(setting_value AS CHAR) FROM settings
            WHERE setting_key IN (@HeaderLinesKey, @FooterLinesKey)
              AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            ORDER BY branch_id IS NULL
            """, new { HeaderLinesKey, FooterLinesKey, branchId }, cancellationToken: ct));
        var values = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Value);

        return new ReceiptTextDto(
            values.TryGetValue(HeaderLinesKey, out var header) ? ParseLines(HeaderLinesKey, header) : [nodeName],
            values.TryGetValue(FooterLinesKey, out var footer) ? ParseLines(FooterLinesKey, footer) : [ReceiptDefaults.FooterLine]);
    }

    /// <summary>A JSON array of strings; a single string is accepted as one line.</summary>
    internal static IReadOnlyList<string> ParseLines(string key, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString()!
                    : throw new FormatException($"Setting {key} must be an array of strings.")).ToList(),
                JsonValueKind.String => [doc.RootElement.GetString()!],
                _ => throw new FormatException($"Setting {key} must be an array of strings."),
            };
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Setting {key} is not valid JSON.", ex);
        }
    }

    private sealed record HeaderRow(
        Guid Id, Guid NodeId, string Number, DateTime SoldAt, string TimeZone, Guid BranchId, string NodeName,
        Guid CashierId, string CashierUsername, string CashierName, Guid? CustomerId, string? CustomerCode, string? CustomerName,
        decimal Subtotal, decimal DiscountAmount, decimal? DiscountPct, decimal RoundingAmount, decimal Total, decimal CreditAmount,
        DateTime? DueDate, string? Notes);
}
