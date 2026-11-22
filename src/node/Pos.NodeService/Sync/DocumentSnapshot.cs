using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Pos.Shared.Sync;

namespace Pos.NodeService.Sync;

/// <summary>
/// The outbox payload of a document (docs/sync-contract.md, format 2): the document's rows exactly as stored, table by
/// table in foreign-key order — the root row, its lines, its stock and loyalty ledger rows and, for documents made on a
/// device, that device row (HQ has no devices of its own). HQ stores them as copies in the same tables.
/// Must match the HQ whitelist <c>App\Sync\DocumentTypes</c>.
/// </summary>
public static class DocumentSnapshot
{
    public const int Format = 2;

    /// <summary>How a table's rows belong to the document: by its id, by a parent column, by doc_id, or the root's device.</summary>
    public sealed record TablePart(string Table, string Link)
    {
        public const string Root = "root";
        public const string Doc = "doc";
        public const string Device = "device";
    }

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<TablePart>> Types = new Dictionary<string, IReadOnlyList<TablePart>>
    {
        [SyncDocTypes.CashierShift] = [new("devices", TablePart.Device), new("cashier_shifts", TablePart.Root)],
        [SyncDocTypes.Sale] =
        [
            new("sales", TablePart.Root), new("sale_lines", "sale_id"), new("sale_payments", "sale_id"),
            new("stock_ledger", TablePart.Doc), new("loyalty_point_ledger", TablePart.Doc),
        ],
        [SyncDocTypes.SaleVoid] = [new("sale_voids", TablePart.Root), new("stock_ledger", TablePart.Doc), new("loyalty_point_ledger", TablePart.Doc)],
        [SyncDocTypes.SaleReturn] =
        [
            new("devices", TablePart.Device), new("sale_returns", TablePart.Root), new("sale_return_lines", "sale_return_id"),
            new("stock_ledger", TablePart.Doc), new("loyalty_point_ledger", TablePart.Doc),
        ],
        [SyncDocTypes.CartCancellation] =
            [new("devices", TablePart.Device), new("cart_cancellations", TablePart.Root), new("cart_cancellation_lines", "cart_cancellation_id")],
        [SyncDocTypes.LocationTransfer] =
            [new("location_transfers", TablePart.Root), new("location_transfer_lines", "location_transfer_id"), new("stock_ledger", TablePart.Doc)],
        [SyncDocTypes.StockAdjustment] =
            [new("stock_adjustments", TablePart.Root), new("stock_adjustment_lines", "stock_adjustment_id"), new("stock_ledger", TablePart.Doc)],
    };

    private static readonly ConcurrentDictionary<string, HashSet<string>> Generated = new();

    /// <summary>Reads the document as it is now (inside the caller's transaction when given).</summary>
    /// <exception cref="InvalidOperationException">Unknown doc type, or the document row does not exist.</exception>
    public static async Task<JsonObject> ReadAsync(MySqlConnection c, MySqlTransaction? tx, string docType, Guid docId, CancellationToken ct)
    {
        if (!Types.TryGetValue(docType, out var parts))
            throw new InvalidOperationException($"No snapshot for document type {docType}.");

        var rootPart = parts.Single(p => p.Link == TablePart.Root);
        var root = await RowsAsync(c, tx, rootPart.Table, "id", docId, ct);
        if (root.Count != 1)
            throw new InvalidOperationException($"{docType} {docId}: the {rootPart.Table} row does not exist.");

        var tables = new JsonArray();
        foreach (var part in parts)
        {
            var rows = part.Link switch
            {
                TablePart.Root => root,
                TablePart.Doc => await RowsAsync(c, tx, part.Table, "doc_id", docId, ct),
                TablePart.Device => root[0]["device_id"] is JsonValue device && Guid.TryParse(device.GetValue<string>(), out var deviceId)
                    ? await RowsAsync(c, tx, part.Table, "id", deviceId, ct)
                    : [],
                _ => await RowsAsync(c, tx, part.Table, part.Link, docId, ct),
            };
            if (rows.Count > 0)
                tables.Add(new JsonObject { ["table"] = part.Table, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()) });
        }

        return new JsonObject { ["format"] = Format, ["node_id"] = root[0]["node_id"]?.DeepClone(), ["tables"] = tables };
    }

    private static async Task<List<JsonObject>> RowsAsync(MySqlConnection c, MySqlTransaction? tx, string table, string column, Guid id, CancellationToken ct)
    {
        var generated = await GeneratedColumnsAsync(c, tx, table, ct);
        await using var command = new MySqlCommand($"SELECT * FROM `{table}` WHERE `{column}` = @id ORDER BY id", c, tx);
        command.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<JsonObject>();
        while (await reader.ReadAsync(ct))
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (!generated.Contains(name))
                    row[name] = Value(reader, i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Values as the contract says: decimals and times as strings, booleans and integers as numbers.</summary>
    internal static JsonNode? Value(MySqlDataReader reader, int i)
    {
        if (reader.IsDBNull(i))
            return null;
        var type = reader.GetDataTypeName(i).ToUpperInvariant();
        var value = reader.GetValue(i);
        return value switch
        {
            DateTime d when type == "DATE" => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DateTime d => JsonValue.Create(d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
            DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            decimal m => JsonValue.Create(m.ToString(CultureInfo.InvariantCulture)),
            bool b => JsonValue.Create(b ? 1 : 0),
            Guid g => JsonValue.Create(g.ToString()),
            sbyte or byte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong u => JsonValue.Create(u),
            float or double => throw new InvalidOperationException($"Column {reader.GetName(i)} is floating point; money and quantities are DECIMAL."),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    internal static async Task<HashSet<string>> GeneratedColumnsAsync(MySqlConnection c, MySqlTransaction? tx, string table, CancellationToken ct)
    {
        if (Generated.TryGetValue(table, out var cached))
            return cached;
        var names = await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND EXTRA LIKE '%GENERATED%'
            """, new { table }, tx, cancellationToken: ct));
        return Generated[table] = names.ToHashSet(StringComparer.Ordinal);
    }
}
