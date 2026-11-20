using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

/// <summary>No database needed.</summary>
public class TestDatabaseTests
{
    private const string Test = "Server=localhost;Database=pos_test;User=u;Password=p";
    private const string Dev = "Server=localhost;Database=pos_dev;User=u;Password=p";

    [Fact]
    public void Not_configured_is_skipped_with_reason()
    {
        var (cs, reason) = TestDatabase.Resolve(null, null);

        Assert.Null(cs);
        Assert.Contains(TestDatabase.EnvironmentVariable, reason);
        Assert.Contains(TestDatabase.SettingsFile, reason);
    }

    [Fact]
    public void Environment_variable_wins_over_file()
    {
        var other = Test.Replace("pos_test", "other_test");
        Assert.Equal(Test, TestDatabase.Resolve(Test, other).ConnectionString);
    }

    [Fact]
    public void File_is_used_when_environment_variable_is_missing()
    {
        Assert.Equal(Test, TestDatabase.Resolve("  ", Test).ConnectionString);
    }

    [Fact]
    public void Non_test_database_is_refused()
    {
        var (cs, reason) = TestDatabase.Resolve(Dev, null);

        Assert.Null(cs);
        Assert.Contains("pos_dev", reason);
    }
}
