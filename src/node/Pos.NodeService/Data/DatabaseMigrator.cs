using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.MySql;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pos.NodeService.Data;

/// <param name="Successful">False when any script failed; nothing after it ran.</param>
/// <param name="ScriptsApplied">Scripts executed by this run (empty when already up to date).</param>
/// <param name="FailedScript">Name of the failing script, if any.</param>
public sealed record MigrationOutcome(
    bool Successful,
    IReadOnlyList<string> ScriptsApplied,
    string? FailedScript,
    string? Error);

/// <summary>
/// Applies <c>database/migrations/NNNN_*.sql</c> in name order with DbUp, journaled in <c>schema_versions</c>.
/// </summary>
public sealed class DatabaseMigrator(string connectionString, string scriptsPath, ILogger? logger = null)
{
    public const string JournalTable = "schema_versions";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public MigrationOutcome Migrate()
    {
        if (!Directory.Exists(scriptsPath))
            return new MigrationOutcome(false, [], null, $"Migrations folder not found: {scriptsPath}");

        DatabaseUpgradeResult result;
        try
        {
            result = Build().PerformUpgrade();
        }
        catch (Exception ex)
        {
            return new MigrationOutcome(false, [], null, ex.Message);
        }

        return new MigrationOutcome(
            result.Successful,
            result.Scripts.Select(s => s.Name).ToList(),
            result.ErrorScript?.Name,
            result.Error?.Message);
    }

    /// <summary>Scripts recorded in the journal (throws when the database is unreachable).</summary>
    public IReadOnlyList<string> GetAppliedScripts() => Build().GetExecutedScripts();

    /// <summary>"0001_initial_schema.sql" -> "0001".</summary>
    public static string VersionOf(string scriptName)
    {
        var underscore = scriptName.IndexOf('_');
        return underscore > 0 && scriptName[..underscore].All(char.IsAsciiDigit) ? scriptName[..underscore] : scriptName;
    }

    private UpgradeEngine Build() =>
        DeployChanges.To
            .MySqlDatabase(DbConnectionFactory.Normalize(connectionString))
            .WithScriptsFromFileSystem(scriptsPath)
            .JournalTo((connectionManager, log) =>
                new UtcMySqlTableJournal(connectionManager, log, DatabaseName(connectionString), JournalTable))
            // Same rule as DbConnectionFactory: the migration session runs in UTC.
            .WithPreprocessor(new UtcSessionPreprocessor())
            .LogTo(new LoggerUpgradeLog(_logger))
            .Build();

    private static string DatabaseName(string connectionString) =>
        new MySqlConnector.MySqlConnectionStringBuilder(connectionString).Database;

    /// <summary>
    /// DbUp passes DateTime.Now (Local) for the "applied" column, which the UTC connection rejects.
    /// Let the server stamp it in UTC instead; the unused parameter is never sent.
    /// </summary>
    private sealed class UtcMySqlTableJournal(
        Func<DbUp.Engine.Transactions.IConnectionManager> connectionManager,
        Func<IUpgradeLog> logger,
        string schema,
        string table) : MySqlTableJournal(connectionManager, logger, schema, table)
    {
        protected override string GetInsertJournalEntrySql(string scriptName, string applied) =>
            $"INSERT INTO {FqSchemaTableName} (scriptname, applied) VALUES ({scriptName}, UTC_TIMESTAMP())";
    }

    private sealed class UtcSessionPreprocessor : IScriptPreprocessor
    {
        public string Process(string contents) => "SET time_zone = '+00:00';\n" + contents;
    }

    private sealed class LoggerUpgradeLog(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);
        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);
        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);
        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
