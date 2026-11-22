using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Sync;

namespace Pos.IntegrationTests;

/// <summary>
/// init-from-hq on a new node: an empty, migrated database gets every master row from HQ, refuses to sell until then,
/// and afterwards sells with HQ's ids — the sale reaches HQ. The shared pos_test is emptied for this test and rebuilt
/// with the dev seed afterwards, so the other tests are unaffected.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SyncInitFromHqTests(DatabaseFixture db)
{
    private static string Id() => Guid.CreateVersion7().ToString();

    [DatabaseFact]
    public async Task An_empty_node_is_filled_from_hq_then_sells_and_sends_the_sale()
    {
        await db.EmptyAsync();
        await using var hq = await FakeHq.StartAsync();
        try
        {
            // HQ's master data for node TK01 (as pos:setup + the web would create it).
            string entity = Id(), branch = Id(), node = Id(), other = Id(), display = Id(), role = Id(), cashier = Id(), unit = Id(),
                product = Id(), productUnit = Id(), cash = Id();
            hq.AddChange("entities", new JsonObject { ["id"] = entity, ["code"] = "E01", ["name"] = "PT Pusat", ["deleted_at"] = null });
            hq.AddChange("branches", new JsonObject { ["id"] = branch, ["entity_id"] = entity, ["code"] = "B01", ["name"] = "Cabang", ["timezone"] = "Asia/Jakarta", ["deleted_at"] = null });
            // The location comes in the same page as its node, but before it in version order: FK checks are off per page.
            hq.AddChange("locations", new JsonObject { ["id"] = display, ["node_id"] = node, ["code"] = "DISPLAY", ["name"] = "Display", ["type"] = "DISPLAY", ["is_sellable"] = 1, ["deleted_at"] = null });
            hq.AddChange("nodes", new JsonObject { ["id"] = node, ["branch_id"] = branch, ["code"] = "TK01", ["name"] = "Toko 1", ["type"] = "STORE", ["is_active"] = 1, ["deleted_at"] = null });
            hq.AddChange("nodes", new JsonObject { ["id"] = other, ["branch_id"] = branch, ["code"] = "TK02", ["name"] = "Toko 2", ["type"] = "STORE", ["is_active"] = 1, ["deleted_at"] = null });
            hq.AddChange("roles", new JsonObject { ["id"] = role, ["code"] = "CASHIER", ["name"] = "Kasir", ["permissions"] = "[\"shift.open\",\"sale.create\"]", ["deleted_at"] = null });
            hq.AddChange("users", new JsonObject { ["id"] = cashier, ["username"] = "kasir.hq", ["password_hash"] = "$2y$04$abcdefghijklmnopqrstuuVH8bU3R7eQvUcD3k7S6wZ1oUq8x2k5a", ["full_name"] = "Kasir HQ", ["is_active"] = 1, ["deleted_at"] = null });
            hq.AddChange("user_roles", new JsonObject { ["id"] = Id(), ["user_id"] = cashier, ["role_id"] = role, ["node_id"] = node, ["deleted_at"] = null });
            hq.AddChange("units", new JsonObject { ["id"] = unit, ["code"] = "PCS", ["name"] = "Pieces", ["allow_decimal"] = 0, ["deleted_at"] = null });
            hq.AddChange("products", new JsonObject { ["id"] = product, ["sku"] = "SKU-1", ["name"] = "Teh Botol", ["base_unit_id"] = unit, ["status"] = "ACTIVE", ["deleted_at"] = null });
            hq.AddChange("product_units", new JsonObject { ["id"] = productUnit, ["product_id"] = product, ["unit_id"] = unit, ["conversion"] = "1.0000", ["is_default_sale"] = 1, ["deleted_at"] = null });
            hq.AddChange("product_barcodes", new JsonObject { ["id"] = Id(), ["product_unit_id"] = productUnit, ["barcode"] = "8990001", ["deleted_at"] = null });
            hq.AddChange("prices", new JsonObject { ["id"] = Id(), ["product_unit_id"] = productUnit, ["branch_id"] = null, ["price"] = "4000.0000", ["min_qty"] = "1.0000", ["effective_from"] = "2026-01-01 00:00:00.000", ["created_by"] = cashier, ["deleted_at"] = null });
            hq.AddChange("payment_methods", new JsonObject { ["id"] = cash, ["code"] = "CASH", ["name"] = "TUNAI", ["is_cash"] = 1, ["is_credit"] = 0, ["is_active"] = 1, ["deleted_at"] = null });
            hq.AddChange("settings", new JsonObject { ["id"] = Id(), ["branch_id"] = null, ["setting_key"] = "shift.blind_count", ["setting_value"] = "false", ["deleted_at"] = null });

            await using var factory = new NodeServiceFactory(db, settings: new Dictionary<string, string?>
            {
                ["Sync:HqUrl"] = hq.Url,
                ["Sync:PullLimit"] = "4",                               // several pages
            });
            new SyncTokenStore(Path.Combine(Path.GetDirectoryName(db.KeyPath)!, "sync-token.dat")).Save(FakeHq.Token);
            using var client = factory.CreateClient();
            var token = db.TokenFor(Guid.Parse(cashier), "kasir.hq");

            // Before: the node does not sell (it does not even know its own node row yet).
            var (refused, problem) = await client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token, Guid.CreateVersion7());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused);
            Assert.Contains(problem.Str("code"), new[] { "node_not_configured", "node_not_initialized" });

            var result = await factory.Services.GetRequiredService<NodeInitializer>().RunAsync(CancellationToken.None);
            Assert.True(result.Success, result.Message);
            Assert.Equal(15, result.Rows);
            await using (var c = await db.Factory.OpenAsync())
            {
                Assert.Equal("HQ", await c.ExecuteScalarAsync<string>("SELECT state_value FROM sync_state WHERE state_key = 'node.initialized'"));
                Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM nodes"));
                Assert.Equal("Asia/Jakarta", await c.ExecuteScalarAsync<string>("SELECT timezone FROM branches WHERE id = @branch", new { branch }));
            }
            Assert.Contains("already initialized", (await factory.Services.GetRequiredService<NodeInitializer>().RunAsync(CancellationToken.None)).Message);

            // After: a cashier PC is registered (add-device), the HQ cashier sells HQ's product; the sale reaches HQ.
            var device = await AddDeviceAsync(Guid.Parse(node));
            var (opened, shift) = await client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token, device);
            Assert.Equal(HttpStatusCode.Created, opened);
            var (_, found) = await client.GetAsync("/api/v1/products/search?q=8990001", token);
            Assert.Equal(4000m, found[0].Dec("price"));
            var sale = Guid.CreateVersion7();
            var (sold, _) = await client.PostAsync("/api/v1/sales", new
            {
                Id = sale,
                Lines = new[] { new { ProductUnitId = Guid.Parse(productUnit), Qty = 2m } },
                Payments = new[] { new { Method = "CASH", Amount = 8000m } },
            }, token, device);
            Assert.Equal(HttpStatusCode.Created, sold);

            await factory.Services.GetRequiredService<SyncAgent>().RunOnceAsync(CancellationToken.None);
            Assert.Equal("8000.0000", hq.Row(sale.ToString(), "sales")!["total"]!.GetValue<string>());
            Assert.Equal(node, hq.Row(sale.ToString(), "sales")!["node_id"]!.GetValue<string>());
            Assert.Equal(display, hq.Row(sale.ToString(), "sale_lines")!["location_id"]!.GetValue<string>());
            Assert.True(hq.Documents.ContainsKey(shift.Id().ToString()));
        }
        finally
        {
            await db.InitializeAsync();                                 // back to the dev seed for the other tests
        }
    }

    private async Task<Guid> AddDeviceAsync(Guid node)
    {
        var id = Guid.CreateVersion7();
        await using var c = await db.Factory.OpenAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO devices (id, node_id, name, type, fingerprint, public_key, status)
            VALUES (@id, @node, 'Kasir PC 1', 'CASHIER_PC', @fingerprint, '', 'ACTIVE')
            """, new { id, node, fingerprint = "T-" + id.ToString("N") });
        return id;
    }
}
