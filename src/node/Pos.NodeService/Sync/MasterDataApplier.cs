using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Pos.NodeService.Data;

namespace Pos.NodeService.Sync;

/// <summary>
/// Applies a pull page from HQ (master data, HQ owns every row): upsert by id, deleted rows too (deleted_at = soft
/// delete here as well), and the new cursor in sync_state — all in one transaction, so a page is applied fully or not
/// at all and a crash never moves the cursor past rows that were not stored. A row arrives with its latest version
/// only, so a parent may come in a later page: foreign key checks are off while a page is applied (HQ never
/// hard-deletes, every parent arrives). Columns this node does not know (HQ newer) are ignored; generated columns are
/// never written.
/// </summary>
public sealed class MasterDataApplier(DbConnectionFactory connections)
{
    /// <summary>docs/sync-contract.md: FK-safe order of the master tables.</summary>
    public static readonly IReadOnlyList<string> TableOrder =
    [
        "entities", "branches", "nodes", "locations", "roles", "users", "user_roles", "units", "categories", "brands",
        "products", "product_units", "product_barcodes", "prices", "promotions", "contacts", "sales_reps",
        "supplier_products", "payment_methods", "reasons", "expense_types", "settings",
    ];

    private readonly ConcurrentDictionary<string, HashSet<string>> _columns = new();

    /// <returns>The tables that received rows.</returns>
    public async Task<IReadOnlySet<string>> ApplyAsync(PullPage page, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS = 0", cancellationToken: ct));
        try
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var order = TableOrder.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
            foreach (var change in page.Changes
                         .Where(ch => order.ContainsKey(ch.Table))       // a table this node does not sync: ignore
                         .OrderBy(ch => order[ch.Table]).ThenBy(ch => ch.Version))
            {
                await UpsertAsync(c, tx, change.Table, change.Row, ct);
                changed.Add(change.Table);
            }
            await SyncState.SetAsync(c, tx, SyncState.MasterVersion, page.NextSince.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            await tx.CommitAsync(ct);
            return changed;
        }
        finally
        {
            // The connection goes back to the pool: never leave foreign key checks off.
            await c.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS = 1", cancellationToken: CancellationToken.None));
        }
    }

    private async Task UpsertAsync(MySqlConnection c, MySqlTransaction tx, string table, JsonObject row, CancellationToken ct)
    {
        var known = await ColumnsAsync(c, tx, table, ct);
        var columns = row.Select(p => p.Key).Where(known.Contains).ToList();
        if (!columns.Contains("id"))
            throw new InvalidOperationException($"HQ sent a {table} row without id.");

        var parameters = new DynamicParameters();
        for (var i = 0; i < columns.Count; i++)
            parameters.Add("p" + i, Value(row[columns[i]]));

        var names = string.Join(", ", columns.Select(col => $"`{col}`"));
        var values = string.Join(", ", columns.Select((_, i) => "@p" + i));
        var updates = string.Join(", ", columns.Where(col => col != "id").Select(col => $"`{col}` = incoming.`{col}`"));
        var sql = $"INSERT INTO `{table}` ({names}) VALUES ({values}) AS incoming"
                  + (updates.Length == 0 ? " ON DUPLICATE KEY UPDATE `id` = incoming.`id`" : $" ON DUPLICATE KEY UPDATE {updates}");
        await c.ExecuteAsync(new CommandDefinition(sql, parameters, tx, cancellationToken: ct));
    }

    /// <summary>Strings stay strings (MySQL converts them to the column type: decimals, times, JSON); numbers as numbers.</summary>
    private static object? Value(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => 1,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l : v.GetValue<decimal>(),
        _ => node.ToJsonString(),
    };

    private async Task<HashSet<string>> ColumnsAsync(MySqlConnection c, MySqlTransaction tx, string table, CancellationToken ct)
    {
        if (_columns.TryGetValue(table, out var cached))
            return cached;
        var names = await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND EXTRA NOT LIKE '%GENERATED%'
            """, new { table }, tx, cancellationToken: ct));
        return _columns[table] = names.ToHashSet(StringComparer.Ordinal);
    }
}
