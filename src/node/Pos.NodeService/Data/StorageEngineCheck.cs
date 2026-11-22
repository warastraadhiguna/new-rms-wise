using Dapper;

namespace Pos.NodeService.Data;

/// <summary>
/// Every table of the node database must be InnoDB (transactions, row locks, foreign keys). A MyISAM table — e.g.
/// created on a server whose default engine is MyISAM — silently loses those guarantees, so the Node Service refuses
/// to start (checked after the migrations).
/// </summary>
public static class StorageEngineCheck
{
    /// <returns>"table (ENGINE)" for every base table of the current database that is not InnoDB; empty when all are.</returns>
    public static async Task<IReadOnlyList<string>> NonInnoDbTablesAsync(DbConnectionFactory connections, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<(string Table, string? Engine)>(new CommandDefinition(
            """
            SELECT table_name, engine FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' AND (engine IS NULL OR engine <> 'InnoDB')
            ORDER BY table_name
            """, cancellationToken: cancellationToken)))
            .Select(t => $"{t.Table} ({t.Engine ?? "unknown"})")
            .ToList();
    }
}
