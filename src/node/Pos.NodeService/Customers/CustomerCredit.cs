using Dapper;
using MySqlConnector;

namespace Pos.NodeService.Customers;

/// <summary>
/// Customer balances from local data only: credit of non-voided sales minus receivable payments recorded
/// at this node (replicas from other stores included once they are synced), and loyalty points.
/// </summary>
public static class CustomerCredit
{
    public static async Task<IReadOnlyDictionary<Guid, decimal>> OutstandingAsync(
        MySqlConnection c, MySqlTransaction? tx, IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        var result = customerIds.Distinct().ToDictionary(id => id, _ => 0m);
        if (result.Count == 0)
            return result;

        var credit = await c.QueryAsync<(Guid CustomerId, decimal Amount)>(new CommandDefinition(
            """
            SELECT s.customer_id, SUM(s.credit_amount) FROM sales s
            WHERE s.customer_id IN @ids AND NOT EXISTS (SELECT 1 FROM sale_voids v WHERE v.sale_id = s.id)
            GROUP BY s.customer_id
            """, new { ids = result.Keys.ToList() }, tx, cancellationToken: ct));
        var paid = await c.QueryAsync<(Guid CustomerId, decimal Amount)>(new CommandDefinition(
            "SELECT customer_id, SUM(amount) FROM receivable_payments WHERE customer_id IN @ids GROUP BY customer_id",
            new { ids = result.Keys.ToList() }, tx, cancellationToken: ct));

        foreach (var row in credit)
            result[row.CustomerId] += row.Amount;
        foreach (var row in paid)
            result[row.CustomerId] -= row.Amount;
        return result;
    }

    public static async Task<decimal> OutstandingAsync(MySqlConnection c, MySqlTransaction? tx, Guid customerId, CancellationToken ct) =>
        (await OutstandingAsync(c, tx, [customerId], ct))[customerId];

    public static async Task<IReadOnlyDictionary<Guid, int>> PointsAsync(
        MySqlConnection c, MySqlTransaction? tx, IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        var result = customerIds.Distinct().ToDictionary(id => id, _ => 0);
        if (result.Count == 0)
            return result;

        var rows = await c.QueryAsync<(Guid CustomerId, decimal Points)>(new CommandDefinition(
            "SELECT customer_id, SUM(points) FROM loyalty_point_ledger WHERE customer_id IN @ids GROUP BY customer_id",
            new { ids = result.Keys.ToList() }, tx, cancellationToken: ct));
        foreach (var row in rows)
            result[row.CustomerId] = (int)row.Points;
        return result;
    }
}
