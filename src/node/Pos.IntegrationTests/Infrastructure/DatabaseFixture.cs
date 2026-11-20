using Dapper;
using Pos.NodeService.Data;

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

    public Guid EntityId { get; } = Guid.CreateVersion7();
    public Guid BranchId { get; } = Guid.CreateVersion7();
    public Guid NodeId { get; } = Guid.CreateVersion7();
    public Guid LocationA { get; } = Guid.CreateVersion7();
    public Guid LocationB { get; } = Guid.CreateVersion7();
    public Guid UnitId { get; } = Guid.CreateVersion7();

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

        await SeedAsync();
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

    private async Task SeedAsync()
    {
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO entities (id, code, name) VALUES (@EntityId, 'E01', 'Test entity')", this);
        await connection.ExecuteAsync(
            "INSERT INTO branches (id, entity_id, code, name, timezone) VALUES (@BranchId, @EntityId, 'B01', 'Test branch', 'Asia/Jakarta')", this);
        await connection.ExecuteAsync(
            "INSERT INTO nodes (id, branch_id, code, name, type) VALUES (@NodeId, @BranchId, 'TK01', 'Test store', 'STORE')", this);
        await connection.ExecuteAsync(
            """
            INSERT INTO locations (id, node_id, code, name, type, is_sellable) VALUES
              (@LocationA, @NodeId, 'DISPLAY', 'Display', 'DISPLAY', 1),
              (@LocationB, @NodeId, 'STORAGE', 'Storage', 'STORAGE', 0)
            """, this);
        await connection.ExecuteAsync(
            "INSERT INTO units (id, code, name, allow_decimal) VALUES (@UnitId, 'PCS', 'Pieces', 0)", this);
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
