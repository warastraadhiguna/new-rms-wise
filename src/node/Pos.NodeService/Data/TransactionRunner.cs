using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Pos.NodeService.Data;

/// <summary>
/// Runs a unit of work in one READ COMMITTED transaction and re-runs the whole unit on deadlock or
/// lock wait timeout. The work must not keep state between attempts.
/// </summary>
public sealed class TransactionRunner(DbConnectionFactory connectionFactory, ILogger<TransactionRunner>? logger = null)
{
    private readonly TransientRetry _retry = new((ILogger?)logger ?? NullLogger.Instance);

    public DbConnectionFactory ConnectionFactory => connectionFactory;

    public Task<T> RunAsync<T>(
        Func<MySqlConnection, MySqlTransaction, CancellationToken, Task<T>> work,
        string operation,
        CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(async ct =>
        {
            await using var connection = await connectionFactory.OpenAsync(ct);
            // READ COMMITTED: locking reads see the latest committed rows and take no gap locks.
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await work(connection, transaction, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, operation, cancellationToken);
}
