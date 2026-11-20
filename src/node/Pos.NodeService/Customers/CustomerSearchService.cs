using Dapper;
using Pos.NodeService.Api;
using Pos.NodeService.Catalog;
using Pos.NodeService.Data;

namespace Pos.NodeService.Customers;

/// <param name="CreditLimit">Null = no credit facility.</param>
/// <param name="CreditOutstanding">Unpaid credit known at this node.</param>
/// <param name="CreditRemaining">Limit minus outstanding (may be negative when over the limit); null without facility.</param>
/// <param name="Points">Loyalty point balance at this node.</param>
public sealed record CustomerSearchItem(
    Guid Id,
    string Code,
    string Name,
    string? Phone,
    decimal? CreditLimit,
    decimal CreditOutstanding,
    decimal? CreditRemaining,
    short? PaymentTermDays,
    int Points);

/// <summary>Customer lookup by code (exact or prefix), phone (contains) or name (every word). At most 50 rows.</summary>
public sealed class CustomerSearchService(DbConnectionFactory connections)
{
    public const int MaxResults = 50;

    public async Task<IReadOnlyList<CustomerSearchItem>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            throw ApiException.Unprocessable("query_required", "Type a customer code, name or phone number.");

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        var parameters = new DynamicParameters(new { q, codePrefix = ProductSearchService.EscapeLike(q) + "%" });
        var nameConditions = words.Select((w, i) =>
        {
            parameters.Add($"w{i}", "%" + ProductSearchService.EscapeLike(w) + "%");
            return $"name LIKE @w{i} ESCAPE '\\\\'";
        });
        var digits = new string(q.Where(char.IsAsciiDigit).ToArray());
        var phoneCondition = digits.Length >= 4 ? "OR phone LIKE @phone" : "";
        parameters.Add("phone", "%" + digits + "%");

        await using var c = await connections.OpenAsync(ct);
        var rows = (await c.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT id AS Id, code AS Code, name AS Name, phone AS Phone, credit_limit AS CreditLimit, payment_term_days AS PaymentTermDays
            FROM contacts
            WHERE is_customer = 1 AND is_active = 1 AND deleted_at IS NULL
              AND (code = @q OR code LIKE @codePrefix ESCAPE '\\' {phoneCondition} OR ({string.Join(" AND ", nameConditions)}))
            ORDER BY code = @q DESC, name
            LIMIT {MaxResults}
            """, parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
            return [];

        var ids = rows.Select(r => r.Id).ToList();
        var outstanding = await CustomerCredit.OutstandingAsync(c, null, ids, ct);
        var points = await CustomerCredit.PointsAsync(c, null, ids, ct);

        return rows.Select(r => new CustomerSearchItem(
            r.Id, r.Code, r.Name, r.Phone, r.CreditLimit, outstanding[r.Id],
            r.CreditLimit is { } limit ? limit - outstanding[r.Id] : null,
            r.PaymentTermDays, points[r.Id])).ToList();
    }

    private sealed record Row(Guid Id, string Code, string Name, string? Phone, decimal? CreditLimit, short? PaymentTermDays);
}
