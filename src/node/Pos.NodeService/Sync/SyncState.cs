using Dapper;
using MySqlConnector;

namespace Pos.NodeService.Sync;

/// <summary>Keys of the node-local <c>sync_state</c> table.</summary>
public static class SyncState
{
    /// <summary>Highest master version applied from HQ (the pull cursor).</summary>
    public const string MasterVersion = "master.version";

    /// <summary>"HQ" after init-from-hq, "DEV_SEED" after the dev seed. The node sells only when this is set.</summary>
    public const string Initialized = "node.initialized";

    /// <summary>UTC time of the last fully successful sync cycle.</summary>
    public const string LastSuccessAt = "sync.last_success_at";

    public static Task SetAsync(MySqlConnection c, MySqlTransaction? tx, string key, string value, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sync_state (state_key, state_value) VALUES (@key, @value) AS incoming
            ON DUPLICATE KEY UPDATE state_value = incoming.state_value
            """, new { key, value }, tx, cancellationToken: ct));

    public static Task<string?> GetAsync(MySqlConnection c, MySqlTransaction? tx, string key, CancellationToken ct) =>
        c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_value FROM sync_state WHERE state_key = @key", new { key }, tx, cancellationToken: ct));
}
