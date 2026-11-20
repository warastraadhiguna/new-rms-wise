using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class DatabaseTests(DatabaseFixture db)
{
    [DatabaseFact]
    public async Task Migrations_run_on_empty_database()
    {
        // the fixture dropped every table, then migrated
        Assert.True(db.Migration.Successful);
        Assert.Contains("0001_initial_schema.sql", db.Migration.ScriptsApplied);
        Assert.Contains("0002_login_throttle.sql", db.Migration.ScriptsApplied);

        await using var connection = await db.Factory.OpenAsync();
        var tables = (await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("schema_versions", tables);
        Assert.Contains("stock_ledger", tables);
        Assert.Contains("stock_balances", tables);
    }

    [DatabaseFact]
    public void Migrating_again_applies_nothing()
    {
        var migrator = new DatabaseMigrator(db.ConnectionString, DatabaseFixture.MigrationsPath);

        var outcome = migrator.Migrate();

        Assert.True(outcome.Successful);
        Assert.Empty(outcome.ScriptsApplied);
        Assert.Equal("0002_login_throttle.sql", migrator.GetAppliedScripts().Order().Last());
    }

    [DatabaseFact]
    public async Task Migration_journal_time_is_utc()
    {
        await using var connection = await db.Factory.OpenAsync();

        var applied = await connection.ExecuteScalarAsync<DateTime>(
            $"SELECT applied FROM {DatabaseMigrator.JournalTable} WHERE scriptname = '0001_initial_schema.sql'");

        // the fixture migrated at the start of this run
        Assert.InRange(applied, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(1));
    }

    [DatabaseFact]
    public async Task Every_connection_uses_utc_session_time_zone()
    {
        await using (var first = await db.Factory.OpenAsync())
        {
            Assert.Equal("+00:00", await first.ExecuteScalarAsync<string>("SELECT @@session.time_zone"));
            await first.ExecuteAsync("SET time_zone = '+07:00'");
        }

        // the pooled connection is reused: the factory must set UTC again
        await using var second = await db.Factory.OpenAsync();
        Assert.Equal("+00:00", await second.ExecuteScalarAsync<string>("SELECT @@session.time_zone"));
        Assert.Equal(0, await second.ExecuteScalarAsync<long>("SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), NOW())"));
    }
}
