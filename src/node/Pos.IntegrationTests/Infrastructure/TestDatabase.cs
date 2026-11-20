using System.Text.Json;
using MySqlConnector;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>
/// Locates the pos_test connection string: environment variable <c>POS_TEST_DB</c>, else
/// <c>testsettings.Local.json</c> (<c>ConnectionStrings:PosTest</c>) next to the test assembly.
/// </summary>
public static class TestDatabase
{
    public const string EnvironmentVariable = "POS_TEST_DB";
    public const string SettingsFile = "testsettings.Local.json";

    private static readonly Lazy<(string? ConnectionString, string? SkipReason)> Resolved = new(() =>
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), ReadSettingsFile()));

    /// <summary>Null when not configured or unsafe; then <see cref="SkipReason"/> says why.</summary>
    public static string? ConnectionString => Resolved.Value.ConnectionString;

    public static string? SkipReason => Resolved.Value.SkipReason;

    public static bool IsConfigured => ConnectionString is not null;

    /// <summary>
    /// Picks the connection string and refuses databases whose name does not end in "_test",
    /// because the fixture drops every table in it.
    /// </summary>
    public static (string? ConnectionString, string? SkipReason) Resolve(string? fromEnvironment, string? fromFile)
    {
        var connectionString = !string.IsNullOrWhiteSpace(fromEnvironment) ? fromEnvironment
            : !string.IsNullOrWhiteSpace(fromFile) ? fromFile
            : null;

        if (connectionString is null)
            return (null,
                $"Integration tests skipped: set {EnvironmentVariable} or create {SettingsFile} " +
                "with ConnectionStrings:PosTest pointing to the pos_test database.");

        string database;
        try
        {
            database = new MySqlConnectionStringBuilder(connectionString).Database;
        }
        catch (ArgumentException ex)
        {
            return (null, $"Integration tests skipped: invalid test connection string ({ex.Message}).");
        }

        if (!database.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
            return (null,
                $"Integration tests skipped: database '{database}' does not end with '_test'. " +
                "The tests drop every table, so only a dedicated test database is allowed.");

        return (connectionString, null);
    }

    private static string? ReadSettingsFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsFile);
        if (!File.Exists(path))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("ConnectionStrings", out var strings)
               && strings.TryGetProperty("PosTest", out var value)
            ? value.GetString()
            : null;
    }
}

/// <summary>A fact that is skipped (not failed) when no test database is configured.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (!TestDatabase.IsConfigured)
            Skip = TestDatabase.SkipReason;
    }
}
