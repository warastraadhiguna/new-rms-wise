using Dapper;
using Pos.NodeService.Auth;
using Pos.NodeService.Data;
using Pos.NodeService.Dev;
using Pos.Shared.Auth;

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

    /// <summary>Node signing key for this test run (temporary file, deleted afterwards).</summary>
    public string KeyPath { get; } = Path.Combine(Path.GetTempPath(), "pos-test-keys", Guid.NewGuid().ToString("N"), "jwt-signing.key");

    public TokenService Tokens { get; private set; } = null!;

    public static readonly string[] CashierPermissions = [Permissions.ShiftOpen, Permissions.ShiftClose, Permissions.SaleCreate];

    public async Task InitializeAsync()
    {
        if (!TestDatabase.IsConfigured)
            return;

        ConnectionString = TestDatabase.ConnectionString!;
        Factory = new DbConnectionFactory(ConnectionString);
        Tokens = new TokenService(SigningKeyStore.LoadOrCreate(KeyPath), "TK01", TimeSpan.FromHours(12));

        await DropAllTablesAsync();
        Migration = new DatabaseMigrator(ConnectionString, MigrationsPath).Migrate();
        if (!Migration.Successful)
            throw new InvalidOperationException($"Migration failed in {Migration.FailedScript}: {Migration.Error}");

        Seed = await new DevSeeder(new TransactionRunner(Factory), TimeProvider.System).SeedAsync("TK01");
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(KeyPath)!, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    /// <summary>Token for a user without going through login (tests that are not about authentication).</summary>
    public string TokenFor(Guid userId, string username = "test", IEnumerable<string>? permissions = null, DateTime? now = null) =>
        Tokens.Issue(userId, username, permissions ?? CashierPermissions, now ?? DateTime.UtcNow).Token;

    /// <summary>The seeded cashier (kasir1) with cashier permissions.</summary>
    public string CashierToken() => TokenFor(Seed.CashierId, "kasir1");

    /// <summary>
    /// A user with bcrypt hashes (low work factor for speed). Each role is (permissions JSON, node id or null = all nodes).
    /// </summary>
    public async Task<Guid> CreateUserAsync(
        string username, string password, string? pin = null, bool isActive = true, bool deleted = false,
        params (string PermissionsJson, Guid? NodeId)[] roles)
    {
        var userId = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO users (id, username, password_hash, pin_hash, full_name, is_active, deleted_at)
            VALUES (@userId, @username, @passwordHash, @pinHash, 'Test user', @isActive, @deletedAt)
            """,
            new
            {
                userId, username, passwordHash = PasswordHasher.Hash(password, workFactor: 4),
                pinHash = pin is null ? null : PasswordHasher.Hash(pin, workFactor: 4),
                isActive, deletedAt = deleted ? DateTime.UtcNow : (DateTime?)null,
            });

        foreach (var (permissionsJson, nodeId) in roles)
        {
            var roleId = Guid.CreateVersion7();
            await connection.ExecuteAsync("INSERT INTO roles (id, code, name, permissions) VALUES (@roleId, @code, 'Test role', @permissionsJson)",
                new { roleId, code = "R" + roleId.ToString("N")[..20], permissionsJson });
            await connection.ExecuteAsync("INSERT INTO user_roles (id, user_id, role_id, node_id) VALUES (@id, @userId, @roleId, @nodeId)",
                new { id = Guid.CreateVersion7(), userId, roleId, nodeId });
        }
        return userId;
    }

    /// <summary>Another node of the same branch (for "no rights at this node" cases).</summary>
    public async Task<Guid> CreateOtherNodeAsync()
    {
        var id = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync("INSERT INTO nodes (id, branch_id, code, name, type) VALUES (@id, @BranchId, @code, 'Other', 'STORE')",
            new { id, BranchId, code = "X" + Random.Shared.Next(100, 999) });
        return id;
    }

    public async Task<Guid> CreateProductAsync(bool trackExpiry, string name = "Test product", bool allowQtyMultiplier = true, string? sku = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO products (id, sku, name, base_unit_id, track_expiry, allow_qty_multiplier)
            VALUES (@id, @sku, @name, @unit, @track, @allowQtyMultiplier)
            """,
            new { id, sku = sku ?? "T-" + id.ToString("N")[..12], name, unit = UnitId, track = trackExpiry, allowQtyMultiplier });
        return id;
    }

    /// <summary>
    /// A product sold in PCS (conversion 1) with a barcode, a default price (plus optional wholesale tiers)
    /// and optional opening stock per expiry date in the sellable DISPLAY location.
    /// </summary>
    public async Task<TestProduct> CreateSellableProductAsync(
        decimal price, bool trackExpiry = false,
        (DateOnly? Expiry, decimal Qty)[]? stock = null,
        (decimal MinQty, decimal Price)[]? tiers = null,
        string name = "Test product",
        bool allowQtyMultiplier = true,
        decimal storageStock = 0m,
        string? sku = null)
    {
        var productId = await CreateProductAsync(trackExpiry, name, allowQtyMultiplier, sku);
        var productUnitId = Guid.CreateVersion7();
        var barcode = "TB" + productUnitId.ToString("N")[..16];
        var effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var connection = await Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO product_units (id, product_id, unit_id, conversion, is_default_sale) VALUES (@productUnitId, @productId, @UnitId, 1, 1)",
                new { productUnitId, productId, UnitId });
            await connection.ExecuteAsync("INSERT INTO product_barcodes (id, product_unit_id, barcode) VALUES (@id, @productUnitId, @barcode)",
                new { id = Guid.CreateVersion7(), productUnitId, barcode });
            foreach (var (minQty, tierPrice) in new[] { (1m, price) }.Concat(tiers ?? []))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by)
                    VALUES (@id, @productUnitId, NULL, @tierPrice, @minQty, @effectiveFrom, @AdminId)
                    """,
                    new { id = Guid.CreateVersion7(), productUnitId, tierPrice, minQty, effectiveFrom, Seed.AdminId });
            }
        }

        if (stock is { Length: > 0 })
        {
            var docId = Guid.CreateVersion7();
            await new Pos.NodeService.Stock.StockPostingService(Factory).PostAsync(stock.Select(s =>
                new Pos.NodeService.Stock.StockMovement(
                    new Pos.Shared.Stock.StockDocumentLine(NodeId, LocationA, productId, Pos.Shared.Stock.StockDocTypes.Adjustment,
                        docId, Guid.CreateVersion7(), DateTime.UtcNow),
                    Pos.Shared.Stock.StockDirection.In, s.Qty, s.Expiry)).ToList());
        }

        if (storageStock != 0)
        {
            await new Pos.NodeService.Stock.StockPostingService(Factory).PostAsync([
                new Pos.NodeService.Stock.StockMovement(
                    new Pos.Shared.Stock.StockDocumentLine(NodeId, LocationB, productId, Pos.Shared.Stock.StockDocTypes.Adjustment,
                        Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow),
                    Pos.Shared.Stock.StockDirection.In, storageStock)]);
        }

        return new TestProduct(productId, productUnitId, barcode);
    }

    public async Task<Guid> CreateCustomerAsync(decimal? creditLimit, short? paymentTermDays = 14)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO contacts (id, code, is_customer, name, credit_limit, payment_term_days)
            VALUES (@id, @code, 1, 'Test customer', @creditLimit, @paymentTermDays)
            """,
            new { id, code = "TC" + id.ToString("N")[..16], creditLimit, paymentTermDays });
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

public sealed record TestProduct(Guid ProductId, Guid ProductUnitId, string Barcode);

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
