using MySqlConnector;
using Pos.NodeService.Data;

namespace Pos.IntegrationTests;

/// <summary>Node Service helpers that need no database.</summary>
public class NodeServiceUnitTests
{
    [Theory]
    [InlineData("0001_initial_schema.sql", "0001")]
    [InlineData("0012_add_promotions.sql", "0012")]
    [InlineData("noversion.sql", "noversion.sql")]
    public void Migration_version_is_the_numeric_prefix(string script, string expected)
    {
        Assert.Equal(expected, DatabaseMigrator.VersionOf(script));
    }

    [Fact]
    public void Journal_table_is_schema_versions()
    {
        Assert.Equal("schema_versions", DatabaseMigrator.JournalTable);
    }

    [Fact]
    public void Connection_string_is_normalised_for_guid_and_utc()
    {
        var normalised = new MySqlConnectionStringBuilder(
            DbConnectionFactory.Normalize("Server=localhost;Database=pos_test;User=u;Password=p"));

        Assert.Equal(MySqlGuidFormat.Char36, normalised.GuidFormat);
        Assert.Equal(MySqlDateTimeKind.Utc, normalised.DateTimeKind);
        Assert.Equal("pos_test", normalised.Database);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Lock_wait_timeout_must_be_positive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DbConnectionFactory("Server=localhost;Database=x_test;User=u;Password=p", seconds));
    }

    [Fact]
    public void Missing_migrations_folder_fails_with_clear_message()
    {
        var outcome = new DatabaseMigrator("Server=localhost;Database=x_test;User=u;Password=p",
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())).Migrate();

        Assert.False(outcome.Successful);
        Assert.Contains("does-not-exist", outcome.Error);
    }

    [Fact]
    public void Migration_scripts_are_copied_next_to_the_service()
    {
        var scripts = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "migrations"), "*.sql");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "0001_initial_schema.sql");
    }
}
