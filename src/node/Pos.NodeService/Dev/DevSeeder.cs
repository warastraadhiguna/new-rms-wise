using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Pos.NodeService.Auth;
using Pos.NodeService.Data;
using Pos.NodeService.Documents;
using Pos.NodeService.Nodes;
using Pos.NodeService.Stock;
using Pos.NodeService.Sync;
using Pos.Shared.Documents;
using Pos.Shared.Stock;
using Pos.Shared.Sync;

namespace Pos.NodeService.Dev;

/// <summary>DEVELOPMENT ONLY logins created by the dev seed. Never use these outside a *_dev database.</summary>
public static class DevCredentials
{
    public const string AdminPassword = "admin123";
    public const string CashierPassword = "kasir123";
    public const string CashierPin = "123456";
    public const string StoreAdminPassword = "admintoko123";
}

public sealed record DevProduct(string Sku, Guid ProductId, Guid ProductUnitId, string Barcode, decimal Price, bool TrackExpiry);

public sealed record DevSeedResult(
    bool Created,
    Guid BranchId,
    Guid NodeId,
    Guid DisplayLocationId,
    Guid StorageLocationId,
    Guid AdminId,
    Guid CashierId,
    IReadOnlyList<Guid> DeviceIds,
    Guid PcsUnitId,
    Guid CustomerId,
    IReadOnlyDictionary<string, Guid> PaymentMethods,
    IReadOnlyList<DevProduct> Products);

/// <summary>
/// Development data for one store node (NOT a migration): entity, branch, node, locations, users, devices,
/// units, 5 products with prices and barcodes, payment methods, a credit customer, settings, and opening
/// stock posted through a real ADJUSTMENT document. Running it again changes nothing.
/// </summary>
public sealed class DevSeeder(TransactionRunner runner, TimeProvider clock, ILogger? logger = null)
{
    public static readonly string[] ProductSkus = ["AIR-600", "MIE-GRG", "GULA-1KG", "BERAS-KG", "SABUN-01"];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<DevSeedResult> SeedAsync(string nodeCode, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async (connection, tx, ct) =>
        {
            var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM nodes WHERE code = @nodeCode", new { nodeCode }, tx, cancellationToken: ct)) > 0;

            if (exists)
            {
                await UpgradeDevAuthAsync(connection, tx, ct);
                _logger.LogInformation("Dev seed skipped: node {Node} already exists.", nodeCode);
                return await LoadAsync(connection, tx, nodeCode, created: false, ct);
            }

            await InsertMasterDataAsync(connection, tx, nodeCode, ct);
            var seeded = await LoadAsync(connection, tx, nodeCode, created: true, ct);
            await PostOpeningStockAsync(connection, tx, seeded, ct);
            _logger.LogInformation("Dev seed created node {Node} with {Count} products and opening stock.", nodeCode, seeded.Products.Count);
            return seeded;
        }, "Dev seed", cancellationToken);

    private async Task InsertMasterDataAsync(MySqlConnection c, MySqlTransaction tx, string nodeCode, CancellationToken ct)
    {
        Guid Id() => Guid.CreateVersion7();
        Task Exec(string sql, object args) => c.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));

        var entity = Id(); var branch = Id(); var node = Id();
        await Exec("INSERT INTO entities (id, code, name) VALUES (@entity, 'E01', 'PT Dev Retail')", new { entity });
        await Exec("INSERT INTO branches (id, entity_id, code, name, timezone) VALUES (@branch, @entity, 'B01', 'Cabang Dev', 'Asia/Jakarta')", new { branch, entity });
        await Exec("INSERT INTO nodes (id, branch_id, code, name, type) VALUES (@node, @branch, @nodeCode, 'Toko Dev', 'STORE')", new { node, branch, nodeCode });
        await Exec(
            """
            INSERT INTO locations (id, node_id, code, name, type, is_sellable) VALUES
              (@display, @node, 'DISPLAY', 'Display', 'DISPLAY', 1),
              (@storage, @node, 'STORAGE', 'Gudang toko', 'STORAGE', 0)
            """, new { display = Id(), storage = Id(), node });

        var cashierRole = Id(); var adminRole = Id(); var ownerRole = Id(); var hqAdminRole = Id();
        var admin = Id(); var cashier = Id(); var storeAdmin = Id();
        await Exec(
            """
            INSERT INTO roles (id, code, name, permissions) VALUES
              (@cashierRole, 'CASHIER', 'Kasir', @CashierPermissions),
              (@adminRole, 'STORE_ADMIN', 'Admin toko', @StoreAdminPermissions),
              (@ownerRole, 'OWNER', 'Pemilik', @FullPermissions),
              (@hqAdminRole, 'HQ_ADMIN', 'Admin HQ', @FullPermissions)
            """, new { cashierRole, adminRole, ownerRole, hqAdminRole, CashierPermissions, StoreAdminPermissions, FullPermissions });
        // DEV ONLY credentials, see DevCredentials.
        await Exec(
            """
            INSERT INTO users (id, username, password_hash, pin_hash, full_name) VALUES
              (@admin, 'admin', @adminHash, NULL, 'Admin Dev'),
              (@cashier, 'kasir1', @cashierHash, @cashierPin, 'Kasir Satu'),
              (@storeAdmin, 'admintoko', @storeAdminHash, NULL, 'Admin Toko Dev')
            """, new
            {
                admin, cashier, storeAdmin,
                storeAdminHash = PasswordHasher.Hash(DevCredentials.StoreAdminPassword),
                adminHash = PasswordHasher.Hash(DevCredentials.AdminPassword),
                cashierHash = PasswordHasher.Hash(DevCredentials.CashierPassword),
                cashierPin = PasswordHasher.Hash(DevCredentials.CashierPin),
            });
        await Exec(
            """
            INSERT INTO user_roles (id, user_id, role_id, node_id) VALUES
              (@a, @admin, @ownerRole, NULL), (@b, @cashier, @cashierRole, @node), (@c, @storeAdmin, @adminRole, @node)
            """, new { a = Id(), b = Id(), c = Id(), admin, ownerRole, cashier, cashierRole, storeAdmin, adminRole, node });
        await Exec(
            """
            INSERT INTO devices (id, node_id, name, type, fingerprint, public_key, status, approved_by, approved_at) VALUES
              (@d1, @node, 'Kasir PC 1', 'CASHIER_PC', 'DEV-PC-1', 'DEV', 'ACTIVE', @admin, UTC_TIMESTAMP(3)),
              (@d2, @node, 'Kasir PC 2', 'CASHIER_PC', 'DEV-PC-2', 'DEV', 'ACTIVE', @admin, UTC_TIMESTAMP(3)),
              (@d3, @node, 'Kasir PC 3', 'CASHIER_PC', 'DEV-PC-3', 'DEV', 'ACTIVE', @admin, UTC_TIMESTAMP(3))
            """, new { d1 = Id(), d2 = Id(), d3 = Id(), node, admin });

        var pcs = Id(); var box = Id(); var kg = Id();
        await Exec(
            """
            INSERT INTO units (id, code, name, allow_decimal) VALUES (@pcs, 'PCS', 'Pcs', 0), (@box, 'BOX', 'Dus', 0), (@kg, 'KG', 'Kilogram', 1)
            """, new { pcs, box, kg });

        // sku, name, base unit, track expiry, default price, barcode
        (string Sku, string Name, Guid Unit, bool Track, decimal Price, string Barcode)[] products =
        [
            ("AIR-600", "Air mineral 600 ml", pcs, true, 4000m, "8990000000011"),
            ("MIE-GRG", "Mie goreng instan", pcs, true, 3500m, "8990000000028"),
            ("GULA-1KG", "Gula pasir 1 kg", pcs, false, 17500m, "8990000000035"),
            ("BERAS-KG", "Beras curah per kg", kg, false, 14000m, "8990000000042"),
            ("SABUN-01", "Sabun mandi batang", pcs, false, 5000m, "8990000000059"),
        ];
        var effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var p in products)
        {
            var product = Id(); var baseUnit = Id();
            await Exec("INSERT INTO products (id, sku, name, base_unit_id, track_expiry) VALUES (@product, @Sku, @Name, @Unit, @Track)",
                new { product, p.Sku, p.Name, p.Unit, p.Track });
            await Exec(
                "INSERT INTO product_units (id, product_id, unit_id, conversion, is_default_sale) VALUES (@baseUnit, @product, @Unit, 1, 1)",
                new { baseUnit, product, p.Unit });
            await Exec("INSERT INTO product_barcodes (id, product_unit_id, barcode) VALUES (@id, @baseUnit, @Barcode)",
                new { id = Id(), baseUnit, p.Barcode });
            await Exec(
                "INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by) VALUES (@id, @baseUnit, NULL, @Price, 1, @effectiveFrom, @admin)",
                new { id = Id(), baseUnit, p.Price, effectiveFrom, admin });

            if (p.Sku == "AIR-600")
            {
                // wholesale tier for 12+ pcs, and a BOX of 24 with its own barcode and price
                await Exec(
                    "INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by) VALUES (@id, @baseUnit, NULL, 3500, 12, @effectiveFrom, @admin)",
                    new { id = Id(), baseUnit, effectiveFrom, admin });
                var boxUnit = Id();
                await Exec("INSERT INTO product_units (id, product_id, unit_id, conversion) VALUES (@boxUnit, @product, @box, 24)",
                    new { boxUnit, product, box });
                await Exec("INSERT INTO product_barcodes (id, product_unit_id, barcode) VALUES (@id, @boxUnit, '8990000000103')",
                    new { id = Id(), boxUnit });
                await Exec(
                    "INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by) VALUES (@id, @boxUnit, NULL, 80000, 1, @effectiveFrom, @admin)",
                    new { id = Id(), boxUnit, effectiveFrom, admin });
            }

            if (p.Sku == "GULA-1KG")
            {
                // this branch sells sugar a bit higher than the default price
                await Exec(
                    "INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by) VALUES (@id, @baseUnit, @branch, 18000, 1, @effectiveFrom, @admin)",
                    new { id = Id(), baseUnit, branch, effectiveFrom, admin });
            }
        }

        await Exec(
            """
            INSERT INTO payment_methods (id, code, name, is_cash, is_credit) VALUES
              (@cash, 'CASH', 'TUNAI', 1, 0), (@debit, 'DEBIT', 'DEBIT', 0, 0), (@qris, 'QRIS', 'QRIS', 0, 0),
              (@transfer, 'TRANSFER', 'TRANSFER', 0, 0), (@credit, 'CREDIT', 'KREDIT', 0, 1)
            """, new { cash = Id(), debit = Id(), qris = Id(), transfer = Id(), credit = Id() });
        await Exec(
            """
            INSERT INTO contacts (id, code, is_customer, name, phone, credit_limit, payment_term_days)
            VALUES (@id, 'C000001', 1, 'Budi Pelanggan', '081200000001', 500000, 14)
            """, new { id = Id() });
        await Exec(
            """
            INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES
              (@s1, NULL, 'rounding.cash_step', '"100"'),
              (@s2, NULL, 'rounding.cash_mode', '"nearest"'),
              (@s3, NULL, 'currency.decimals', '0'),
              (@s4, NULL, 'returns.window_days', '7')
            """, new { s1 = Id(), s2 = Id(), s3 = Id(), s4 = Id() });
        foreach (var (key, value) in ReceiptSettings)
        {
            await Exec("INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@id, NULL, @key, JSON_QUOTE(@value))",
                new { id = Id(), key, value });
        }
        await Exec("INSERT INTO reasons (id, type, name) VALUES (@id, 'ADJUSTMENT', 'Saldo awal (OPENING)')", new { id = Id() });
    }

    private const string CashierPermissions = """["shift.open","shift.close","sale.create"]""";
    /// <summary>OWNER and HQ_ADMIN only: "*" includes stock.rebuild.</summary>
    private const string FullPermissions = """["*"]""";

    /// <summary>Store admin / supervisor: everything a store needs, but not stock.rebuild (owner's decision).</summary>
    private const string StoreAdminPermissions =
        """["shift.open","shift.close","shift.share","sale.create","sale.discount.override","sale.line.delete","discount.max:100"]""";

    /// <summary>Receipt texts, one setting per field like the RMS 2 company table and footerText1/footerText2.</summary>
    private static readonly (string Key, string Value)[] ReceiptSettings =
    [
        ("receipt.store_name", "TOKO DEV"),
        ("receipt.address", "Jl. Merdeka No. 1"),
        ("receipt.city", "Jakarta"),
        ("receipt.phone", "021-5550123"),
        ("receipt.footer_1", "Terimakasih Atas Kunjungannya"),
        ("receipt.footer_2", "Sampai Jumpa Kembali"),
    ];

    /// <summary>Replaced by the per-field receipt settings (owner revision).</summary>
    private static readonly string[] ObsoleteReceiptKeys = ["receipt.header_lines", "receipt.footer_lines"];

    /// <summary>
    /// Databases seeded before authentication existed: give the dev users real bcrypt hashes and the
    /// dev roles their permissions. Only touches the placeholder values, so it is safe to repeat.
    /// </summary>
    private async Task UpgradeDevAuthAsync(MySqlConnection c, MySqlTransaction tx, CancellationToken ct)
    {
        Task<int> Exec(string sql, object args) => c.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));

        var users =
            await Exec("UPDATE users SET password_hash = @hash WHERE username = 'admin' AND password_hash = '!dev-no-login'",
                new { hash = PasswordHasher.Hash(DevCredentials.AdminPassword) })
            + await Exec("UPDATE users SET password_hash = @hash, pin_hash = @pin WHERE username = 'kasir1' AND password_hash = '!dev-no-login'",
                new { hash = PasswordHasher.Hash(DevCredentials.CashierPassword), pin = PasswordHasher.Hash(DevCredentials.CashierPin) });
        var roles =
            await Exec("UPDATE roles SET permissions = @CashierPermissions WHERE code = 'CASHIER' AND permissions IS NULL", new { CashierPermissions })
            + await Exec("UPDATE roles SET permissions = @StoreAdminPermissions WHERE code = 'STORE_ADMIN' AND permissions IS NULL", new { StoreAdminPermissions });

        if (users + roles > 0)
            _logger.LogInformation("Dev seed: set credentials for {Users} user(s) and permissions for {Roles} role(s).", users, roles);

        // Payment methods as in RMS 2 (TUNAI, DEBIT, QRIS, TRANSFER, KREDIT) for databases seeded earlier.
        var methods =
            await Exec("UPDATE payment_methods SET name = 'TUNAI' WHERE code = 'CASH' AND name = 'Tunai'", new { })
            + await Exec("UPDATE payment_methods SET name = 'KREDIT' WHERE code = 'CREDIT' AND name = 'Kredit / piutang'", new { });
        foreach (var code in new[] { "DEBIT", "TRANSFER" })
        {
            methods += await Exec(
                """
                INSERT INTO payment_methods (id, code, name, is_cash, is_credit)
                SELECT @id, @code, @code, 0, 0 FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM payment_methods WHERE code = @code)
                """, new { id = Guid.CreateVersion7(), code });
        }
        if (methods > 0)
            _logger.LogInformation("Dev seed: updated {Count} payment method(s) to the RMS 2 list.", methods);

        await UpgradeRolesAndReceiptAsync(c, tx, ct);
    }

    /// <summary>
    /// Owner's decision: stock.rebuild only for OWNER and HQ_ADMIN. STORE_ADMIN loses "*" (explicit list), the dev
    /// user admin moves to OWNER, admintoko is the store admin. Also adds the receipt texts. Safe to repeat.
    /// </summary>
    private async Task UpgradeRolesAndReceiptAsync(MySqlConnection c, MySqlTransaction tx, CancellationToken ct)
    {
        Task<int> Exec(string sql, object args) => c.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));

        var changes = 0;
        foreach (var (code, name) in new[] { ("OWNER", "Pemilik"), ("HQ_ADMIN", "Admin HQ") })
        {
            changes += await Exec(
                """
                INSERT INTO roles (id, code, name, permissions)
                SELECT @id, @code, @name, @FullPermissions FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM roles WHERE code = @code)
                """, new { id = Guid.CreateVersion7(), code, name, FullPermissions });
        }
        changes += await Exec(
            "UPDATE roles SET permissions = @StoreAdminPermissions WHERE code = 'STORE_ADMIN' AND JSON_CONTAINS(permissions, '\"*\"')",
            new { StoreAdminPermissions });
        changes += await Exec(
            """
            UPDATE user_roles ur
            JOIN users u ON u.id = ur.user_id AND u.username = 'admin'
            JOIN roles r ON r.id = ur.role_id AND r.code = 'STORE_ADMIN'
            SET ur.role_id = (SELECT id FROM roles WHERE code = 'OWNER'), ur.node_id = NULL
            """, new { });

        var storeAdmin = Guid.CreateVersion7();
        if (await Exec(
                """
                INSERT INTO users (id, username, password_hash, pin_hash, full_name)
                SELECT @storeAdmin, 'admintoko', @hash, NULL, 'Admin Toko Dev' FROM DUAL
                WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'admintoko')
                """, new { storeAdmin, hash = PasswordHasher.Hash(DevCredentials.StoreAdminPassword) }) > 0)
        {
            changes += await Exec(
                """
                INSERT INTO user_roles (id, user_id, role_id, node_id)
                SELECT @id, @storeAdmin, r.id, n.id FROM roles r JOIN nodes n ON n.code = (SELECT MIN(code) FROM nodes)
                WHERE r.code = 'STORE_ADMIN'
                """, new { id = Guid.CreateVersion7(), storeAdmin });
        }

        changes += await Exec(
            "UPDATE settings SET deleted_at = UTC_TIMESTAMP(3), version = version + 1 WHERE setting_key IN @ObsoleteReceiptKeys AND deleted_at IS NULL",
            new { ObsoleteReceiptKeys });
        foreach (var (key, value) in ReceiptSettings)
        {
            changes += await Exec(
                """
                INSERT INTO settings (id, branch_id, setting_key, setting_value)
                SELECT @id, NULL, @key, JSON_QUOTE(@value) FROM DUAL
                WHERE NOT EXISTS (SELECT 1 FROM settings WHERE branch_id IS NULL AND setting_key = @key)
                """, new { id = Guid.CreateVersion7(), key, value });
        }

        if (changes > 0)
            _logger.LogInformation("Dev seed: roles OWNER/HQ_ADMIN/STORE_ADMIN and receipt settings brought up to date ({Count} change(s)).", changes);
    }

    private static async Task<DevSeedResult> LoadAsync(MySqlConnection c, MySqlTransaction tx, string nodeCode, bool created, CancellationToken ct)
    {
        Task<T> One<T>(string sql, object? args = null) =>
            c.QuerySingleAsync<T>(new CommandDefinition(sql, args, tx, cancellationToken: ct));

        var node = await One<(Guid Id, Guid BranchId)>("SELECT id, branch_id FROM nodes WHERE code = @nodeCode", new { nodeCode });
        var display = await One<Guid>("SELECT id FROM locations WHERE node_id = @Id AND code = 'DISPLAY'", new { node.Id });
        var storage = await One<Guid>("SELECT id FROM locations WHERE node_id = @Id AND code = 'STORAGE'", new { node.Id });
        var admin = await One<Guid>("SELECT id FROM users WHERE username = 'admin'");
        var cashier = await One<Guid>("SELECT id FROM users WHERE username = 'kasir1'");
        var devices = (await c.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM devices WHERE node_id = @Id ORDER BY fingerprint", new { node.Id }, tx, cancellationToken: ct))).ToList();
        var pcs = await One<Guid>("SELECT id FROM units WHERE code = 'PCS'");
        var customer = await One<Guid>("SELECT id FROM contacts WHERE code = 'C000001'");
        var methods = (await c.QueryAsync<(string Code, Guid Id)>(new CommandDefinition(
            "SELECT code, id FROM payment_methods", transaction: tx, cancellationToken: ct))).ToDictionary(m => m.Code, m => m.Id);

        var products = new List<DevProduct>();
        foreach (var sku in ProductSkus)
        {
            products.Add(await One<DevProduct>(
                """
                SELECT p.sku AS Sku, p.id AS ProductId, pu.id AS ProductUnitId, pb.barcode AS Barcode, pr.price AS Price, p.track_expiry AS TrackExpiry
                FROM products p
                JOIN product_units pu ON pu.product_id = p.id AND pu.unit_id = p.base_unit_id
                JOIN product_barcodes pb ON pb.product_unit_id = pu.id
                JOIN prices pr ON pr.product_unit_id = pu.id AND pr.branch_id IS NULL AND pr.min_qty = 1
                WHERE p.sku = @sku
                """, new { sku }));
        }

        return new DevSeedResult(created, node.BranchId, node.Id, display, storage, admin, cashier, devices, pcs, customer, methods, products);
    }

    private async Task PostOpeningStockAsync(MySqlConnection c, MySqlTransaction tx, DevSeedResult seed, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var nodeInfo = new NodeInfo(seed.NodeId,
            await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT code FROM nodes WHERE id = @NodeId", new { seed.NodeId }, tx, cancellationToken: ct)) ?? "",
            seed.BranchId,
            await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT timezone FROM branches WHERE id = @BranchId", new { seed.BranchId }, tx, cancellationToken: ct)) ?? "Asia/Jakarta");
        var reason = await c.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT id FROM reasons WHERE type = 'ADJUSTMENT' LIMIT 1", transaction: tx, cancellationToken: ct));
        var bySku = seed.Products.ToDictionary(p => p.Sku);

        // sku, expiry, base qty, unit cost
        (string Sku, DateOnly? Expiry, decimal Qty, decimal Cost)[] opening =
        [
            ("AIR-600", new DateOnly(2027, 3, 1), 24m, 2800m),
            ("AIR-600", new DateOnly(2027, 6, 1), 24m, 2800m),
            ("MIE-GRG", new DateOnly(2027, 2, 1), 40m, 2600m),
            ("GULA-1KG", null, 20m, 15000m),
            ("BERAS-KG", null, 50m, 12000m),
            ("SABUN-01", null, 30m, 3800m),
        ];

        var adjustmentId = Guid.CreateVersion7();
        var lines = opening.Select(o => new AdjustmentLine(Guid.CreateVersion7(), bySku[o.Sku].ProductId, o.Expiry, o.Qty, o.Cost)).ToList();

        var entries = await StockPostingService.PostWithinAsync(c, tx,
            lines.Select(l => new StockMovement(
                new StockDocumentLine(seed.NodeId, seed.DisplayLocationId, l.ProductId, StockDocTypes.Adjustment, adjustmentId, l.Id, now),
                StockDirection.In, l.BaseQty, l.ExpiryDate)).ToList(), ct);

        var number = await DocumentNumbering.NextAsync(c, tx, nodeInfo, DocumentTypes.StockAdjustment, now, ct);

        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO stock_adjustments (id, node_id, number, location_id, reason_id, adjusted_at, notes, created_by)
            VALUES (@adjustmentId, @NodeId, @number, @DisplayLocationId, @reason, @now, 'Dev seed opening stock', @AdminId)
            """, new { adjustmentId, seed.NodeId, number, seed.DisplayLocationId, reason, now, seed.AdminId }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO stock_adjustment_lines (id, stock_adjustment_id, product_id, expiry_date, base_qty, unit_cost)
            VALUES (@Id, @adjustmentId, @ProductId, @ExpiryDate, @BaseQty, @UnitCost)
            """,
            lines.Select(l => new { l.Id, adjustmentId, l.ProductId, ExpiryDate = l.ExpiryDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), l.BaseQty, l.UnitCost }).ToList(),
            tx, cancellationToken: ct));

        await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.StockAdjustment, adjustmentId, 1, new
        {
            Id = adjustmentId,
            seed.NodeId,
            Number = number,
            LocationId = seed.DisplayLocationId,
            ReasonId = reason,
            AdjustedAt = now,
            CreatedBy = seed.AdminId,
            Lines = lines,
            StockLedger = entries,
        }, ct);
    }

    private sealed record AdjustmentLine(Guid Id, Guid ProductId, DateOnly? ExpiryDate, decimal BaseQty, decimal UnitCost);
}
