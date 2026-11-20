using Dapper;
using Pos.NodeService.Data;
using Pos.NodeService.Dev;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>
/// Once per test run: empties pos_test, runs every migration, seeds minimum master data.
/// Tests create their own products so they never share stock.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public static readonly DateTime OccurredAt = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    public DbConnectionFactory Factory { get; private set; } = null!;
    public MigrationOutcome Migration { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Master data created by the dev seeder (node TK01).</summary>
    public DevSeedResult Seed { get; private set; } = null!;

    public Guid BranchId => Seed.BranchId;
    public Guid NodeId => Seed.NodeId;
    public Guid LocationA => Seed.DisplayLocationId;   // sellable
    public Guid LocationB => Seed.StorageLocationId;
    public Guid UnitId => Seed.PcsUnitId;

    public static string MigrationsPath => Path.Combine(AppContext.BaseDirectory, "migrations");

    public async Task InitializeAsync()
    {
        if (!TestDatabase.IsConfigured)
            return;

        ConnectionString = TestDatabase.ConnectionString!;
        Factory = new DbConnectionFactory(ConnectionString);

        await DropAllTablesAsync();
        Migration = new DatabaseMigrator(ConnectionString, MigrationsPath).Migrate();
        if (!Migration.Successful)
            throw new InvalidOperationException($"Migration failed in {Migration.FailedScript}: {Migration.Error}");

        Seed = await new DevSeeder(new TransactionRunner(Factory), TimeProvider.System).SeedAsync("TK01");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task<Guid> CreateProductAsync(bool trackExpiry)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO products (id, sku, name, base_unit_id, track_expiry) VALUES (@id, @sku, @name, @unit, @track)",
            new { id, sku = "T-" + id.ToString("N")[..12], name = "Test product", unit = UnitId, track = trackExpiry });
        return id;
    }

    /// <summary>A fresh ACTIVE device of node TK01, so tests never share open shifts.</summary>
    public async Task<Guid> CreateDeviceAsync(string status = "ACTIVE", Guid? nodeId = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO devices (id, node_id, name, type, fingerprint, public_key, status)
            VALUES (@id, @node, 'Test device', 'CASHIER_PC', @fingerprint, 'TEST', @status)
            """,
            new { id, node = nodeId ?? NodeId, fingerprint = "T-" + id.ToString("N"), status });
        return id;
    }

    private async Task DropAllTablesAsync()
    {
        await using var connection = await Factory.OpenAsync();
        var tables = (await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()")).ToList();

        await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS = 0");
        foreach (var table in tables)
            await connection.ExecuteAsync($"DROP TABLE IF EXISTS `{table.Replace("`", "``")}`");
        await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS = 1");
    }

}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
