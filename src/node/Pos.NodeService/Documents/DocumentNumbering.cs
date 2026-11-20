using Dapper;
using MySqlConnector;
using Pos.NodeService.Nodes;
using Pos.Shared.Documents;

namespace Pos.NodeService.Documents;

/// <summary>
/// Running numbers from <c>document_sequences</c>. The upsert locks the (type, period) row until the
/// document transaction ends, so parallel documents get distinct numbers and a rollback gives the number
/// back (no gaps). Take the number as late as possible in the transaction: the row is a hot spot.
/// </summary>
public static class DocumentNumbering
{
    public static async Task<string> NextAsync(
        MySqlConnection connection, MySqlTransaction transaction, NodeInfo node, string typeCode, DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var period = DocumentNumber.Period(utcNow, node.TimeZone);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document_sequences (doc_type, period, last_number) VALUES (@typeCode, @period, 1)
            ON DUPLICATE KEY UPDATE last_number = last_number + 1
            """,
            new { typeCode, period }, transaction, cancellationToken: cancellationToken));

        var number = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT last_number FROM document_sequences WHERE doc_type = @typeCode AND period = @period",
            new { typeCode, period }, transaction, cancellationToken: cancellationToken));

        return DocumentNumber.Format(node.Code, typeCode, period, number);
    }
}
