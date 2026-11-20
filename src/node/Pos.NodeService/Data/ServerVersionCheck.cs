using System.Text.RegularExpressions;
using Dapper;

namespace Pos.NodeService.Data;

public sealed record ServerVersionCheckResult(bool Supported, string Product, Version? Version, string Raw, string? Reason);

/// <summary>
/// The schema and SQL need MySQL 8.0.19+ (e.g. <c>INSERT ... VALUES (...) AS new</c>). MariaDB is not supported.
/// </summary>
public static partial class ServerVersionCheck
{
    public static readonly Version Minimum = new(8, 0, 19);

    public static async Task<ServerVersionCheckResult> CheckAsync(DbConnectionFactory connections, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var raw = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT VERSION()", cancellationToken: cancellationToken));
        return Evaluate(raw ?? "");
    }

    public static ServerVersionCheckResult Evaluate(string raw)
    {
        if (raw.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
        {
            // MariaDB may prefix "5.5.5-" for old clients; report the real version.
            var mariaVersion = VersionPattern().Matches(raw).Select(m => Version.Parse(m.Value)).LastOrDefault();
            return new ServerVersionCheckResult(false, "MariaDB", mariaVersion, raw,
                $"MariaDB {raw} is not supported; use MySQL {Minimum} or later.");
        }

        var match = VersionPattern().Match(raw);
        if (!match.Success)
            return new ServerVersionCheckResult(false, "Unknown", null, raw, $"Cannot read the server version '{raw}'.");

        var version = Version.Parse(match.Value);
        return version >= Minimum
            ? new ServerVersionCheckResult(true, "MySQL", version, raw, null)
            : new ServerVersionCheckResult(false, "MySQL", version, raw, $"MySQL {version} is too old; {Minimum} or later is required.");
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
