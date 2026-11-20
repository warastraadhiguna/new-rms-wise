using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.NodeService.Sales;

public sealed record SaleSettingsValues(CashRoundingSettings Rounding, int CurrencyDecimals);

/// <summary>
/// Sale-related settings (<c>rounding.cash_step</c>, <c>rounding.cash_mode</c>, <c>currency.decimals</c>):
/// the branch row wins over the global row (branch_id NULL). Values are JSON; numbers may be strings.
/// </summary>
public static class SaleSettings
{
    public static async Task<SaleSettingsValues> LoadAsync(MySqlConnection c, MySqlTransaction? tx, Guid branchId, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            """
            SELECT setting_key, CAST(setting_value AS CHAR) FROM settings
            WHERE setting_key IN ('rounding.cash_step', 'rounding.cash_mode', 'currency.decimals')
              AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            ORDER BY branch_id IS NULL
            """, new { branchId }, tx, cancellationToken: ct));
        var values = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Value);

        var step = values.TryGetValue("rounding.cash_step", out var s) ? JsonDecimal(s) : 0m;
        var mode = values.TryGetValue("rounding.cash_mode", out var m) ? CashRoundingModes.Parse(JsonString(m)) : CashRoundingMode.Nearest;
        var decimals = values.TryGetValue("currency.decimals", out var d) ? (int)JsonDecimal(d) : Rupiah.DefaultCurrencyDecimals;
        return new SaleSettingsValues(new CashRoundingSettings(step, mode), decimals);
    }

    private static decimal JsonDecimal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.String
            ? decimal.Parse(doc.RootElement.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : doc.RootElement.GetDecimal();
    }

    private static string JsonString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetString() ?? "";
    }
}
