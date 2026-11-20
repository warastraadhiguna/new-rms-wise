using MySqlConnector;

namespace Pos.NodeService.Data;

/// <summary>
/// The only way the Node Service opens MySQL connections. Every connection runs in UTC
/// (<c>SET time_zone = '+00:00'</c>) so CURRENT_TIMESTAMP defaults are UTC too.
/// </summary>
public sealed class DbConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _sessionSetup;

    /// <param name="lockWaitTimeoutSeconds">
    /// Optional <c>innodb_lock_wait_timeout</c> for the session (setting <c>Database:LockWaitTimeoutSeconds</c>);
    /// null keeps the server default (50 s).
    /// </param>
    public DbConnectionFactory(string connectionString, int? lockWaitTimeoutSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (lockWaitTimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(lockWaitTimeoutSeconds), lockWaitTimeoutSeconds, "Must be positive.");

        _connectionString = Normalize(connectionString);
        _sessionSetup = lockWaitTimeoutSeconds is { } seconds
            ? $"SET time_zone = '+00:00', innodb_lock_wait_timeout = {seconds}"
            : "SET time_zone = '+00:00'";
    }

    /// <summary>
    /// CHAR(36) columns map to <see cref="Guid"/>; DATETIME values are read as UTC.
    /// </summary>
    public static string Normalize(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.Char36,
            DateTimeKind = MySqlDateTimeKind.Utc,
        }.ConnectionString;

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            // Pooled connections are reset on reuse, which also resets time_zone: set it on every open.
            await using var command = new MySqlCommand(_sessionSetup, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
