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
        Assert.Contains("0003_cashier_screen.sql", db.Migration.ScriptsApplied);
        Assert.Contains("0004_cart_cancellations.sql", db.Migration.ScriptsApplied);
        Assert.Contains("0005_sale_receipt.sql", db.Migration.ScriptsApplied);
        Assert.Contains("0006_location_transfer_lines.sql", db.Migration.ScriptsApplied);

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
        Assert.Equal(DatabaseFixture.LatestMigration, migrator.GetAppliedScripts().Order().Last());
    }


    [DatabaseFact]
    public async Task Every_table_is_innodb_after_the_migrations_including_the_journal()
    {
        Assert.Empty(await StorageEngineCheck.NonInnoDbTablesAsync(db.Factory));
    }

    [DatabaseFact]
    public async Task A_myisam_table_is_reported_so_the_node_refuses_to_start()
    {
        var table = "zz_myisam_" + Guid.NewGuid().ToString("N")[..8];
        await using (var connection = await db.Factory.OpenAsync())
            await connection.ExecuteAsync($"CREATE TABLE {table} (id INT) ENGINE=MyISAM");
        try
        {
            Assert.Equal([$"{table} (MyISAM)"], await StorageEngineCheck.NonInnoDbTablesAsync(db.Factory));
        }
        finally
        {
            await using var connection = await db.Factory.OpenAsync();
            await connection.ExecuteAsync($"DROP TABLE {table}");
        }
    }
    [DatabaseFact]
    public async Task Migration_journal_time_is_utc()
    {
        await using var connection = await db.Factory.OpenAsync();

        var applied = await connection.ExecuteScalarAsync<DateTime>(
            $"SELECT applied FROM {DatabaseMigrator.JournalTable} WHERE scriptname = '0001_initial_schema.sql'");

        // Compared with the moment the fixture migrated, not with "now": a slow or paused run (one took 32 minutes)
        // must not fail. Local time here is UTC+7, so a non-UTC journal would be hours off. The column has whole seconds.
        Assert.InRange(applied, db.MigrationStartedUtc.AddSeconds(-1), db.MigrationFinishedUtc.AddSeconds(1));
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

    /// <summary>
    /// Regression: other nodes used the code "X" + a random 100..998, and nodes.code is unique, so two calls in one run
    /// sometimes collided and failed AuthApiTests before login (7 ms instead of ~70 ms). 200 calls would always collide.
    /// </summary>
    [DatabaseFact]
    public async Task Other_nodes_created_by_tests_never_share_a_code()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 200; i++)
            ids.Add(await db.CreateOtherNodeAsync());

        await using var connection = await db.Factory.OpenAsync();
        var codes = (await connection.QueryAsync<string>("SELECT code FROM nodes WHERE id IN @ids", new { ids })).ToList();
        Assert.Equal(200, codes.Distinct().Count());
        Assert.All(codes, c => Assert.True(c.Length <= 10, c));
    }
}
