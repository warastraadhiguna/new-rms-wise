using System.Text.Json;
using Dapper;
using MySqlConnector;
using Pos.Shared.Sync;

namespace Pos.NodeService.Sync;

/// <summary>
/// Writes the <c>sync_outbox</c> row of a document inside the document's own transaction.
/// The payload is the full document (header + lines) in <see cref="PosJson"/> format.
/// </summary>
public static class OutboxWriter
{
    public static async Task<long> WriteAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string docType, Guid docId, int rowVersion, object payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);
        ArgumentNullException.ThrowIfNull(payload);
        if (rowVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(rowVersion), rowVersion, "Row version starts at 1.");

        var json = JsonSerializer.Serialize(payload, payload.GetType(), PosJson.Options);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO sync_outbox (doc_type, doc_id, row_version, payload) VALUES (@docType, @docId, @rowVersion, @json);
            SELECT LAST_INSERT_ID();
            """,
            new { docType, docId, rowVersion, json }, transaction, cancellationToken: cancellationToken));
    }
}
