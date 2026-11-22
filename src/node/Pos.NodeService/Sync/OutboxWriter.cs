using Dapper;
using MySqlConnector;

namespace Pos.NodeService.Sync;

/// <summary>
/// Writes the <c>sync_outbox</c> row of a document inside the document's own transaction, after its rows.
/// The payload is the document's snapshot (<see cref="DocumentSnapshot"/>, docs/sync-contract.md format 2): the rows
/// exactly as stored, so HQ keeps identical copies.
/// </summary>
public static class OutboxWriter
{
    public static async Task<long> WriteAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string docType, Guid docId, int rowVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);
        if (rowVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(rowVersion), rowVersion, "Row version starts at 1.");

        var json = (await DocumentSnapshot.ReadAsync(connection, transaction, docType, docId, cancellationToken)).ToJsonString();

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO sync_outbox (doc_type, doc_id, row_version, payload) VALUES (@docType, @docId, @rowVersion, @json);
            SELECT LAST_INSERT_ID();
            """,
            new { docType, docId, rowVersion, json }, transaction, cancellationToken: cancellationToken));
    }
}
