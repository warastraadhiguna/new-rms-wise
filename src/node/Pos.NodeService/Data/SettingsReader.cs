using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;

namespace Pos.NodeService.Data;

/// <summary>Reads one setting (JSON value): the branch row wins over the global row (branch_id NULL).</summary>
public static class SettingsReader
{
    public static Task<string?> GetJsonAsync(MySqlConnection c, MySqlTransaction? tx, string key, Guid branchId, CancellationToken ct) =>
        c.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT CAST(setting_value AS CHAR) FROM settings
            WHERE setting_key = @key AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            ORDER BY branch_id IS NULL LIMIT 1
            """, new { key, branchId }, tx, cancellationToken: ct));

    /// <summary>A JSON number or a numeric string; anything else (or missing) gives the default.</summary>
    public static async Task<int> GetIntAsync(MySqlConnection c, MySqlTransaction? tx, string key, Guid branchId, int fallback, CancellationToken ct)
    {
        var json = await GetJsonAsync(c, tx, key, branchId, ct);
        if (json is null)
            return fallback;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Number when doc.RootElement.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(doc.RootElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => fallback,
        };
    }
}

/// <summary>Setting pricing.uniform: true = one price for all branches, branch prices (prices.branch_id) are ignored.</summary>
public static class PricingSettings
{
    public const string UniformKey = "pricing.uniform";

    /// <summary>JSON true/false (or "true"/"false", 1/0); missing = false (prices may differ per branch).</summary>
    public static async Task<bool> UniformAsync(MySqlConnection c, MySqlTransaction? tx, Guid branchId, CancellationToken ct)
    {
        var json = await SettingsReader.GetJsonAsync(c, tx, UniformKey, branchId, ct);
        if (json is null)
            return false;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => doc.RootElement.GetDecimal() != 0,
            JsonValueKind.String => doc.RootElement.GetString() is "true" or "1",
            _ => false,
        };
    }
}
