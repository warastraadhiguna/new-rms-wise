using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

/// <summary>Setting pricing.uniform (12a): true = branch prices are ignored by the product search and by the sale.</summary>
[Collection(DatabaseCollection.Name)]
public class UniformPricingTests(DatabaseFixture db) : IAsyncLifetime
{
    private NodeServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.IsConfigured)
        {
            _factory = new NodeServiceFactory(db);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    /// <summary>Default 10.000 for all branches, 12.000 at this node's branch.</summary>
    private async Task<TestProduct> ProductWithBranchPriceAsync()
    {
        var product = await db.CreateSellableProductAsync(10000m, stock: [(null, 20m)]);
        await ExecAsync(
            """
            INSERT INTO prices (id, product_unit_id, branch_id, price, min_qty, effective_from, created_by)
            VALUES (@id, @ProductUnitId, @BranchId, 12000, 1, UTC_TIMESTAMP(3) - INTERVAL 1 DAY, @AdminId)
            """, new { id = Guid.CreateVersion7(), product.ProductUnitId, db.BranchId, db.Seed.AdminId });
        return product;
    }

    private async Task<(decimal SearchPrice, decimal SaleTotal)> PricesAsync(TestProduct product)
    {
        var (_, found) = await _client.GetAsync($"/api/v1/products/search?q={product.Barcode}", db.CashierToken());
        var device = await db.CreateDeviceAsync();
        await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, db.CashierToken(), device);
        var (status, sale) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = 20000m } },
        }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return (found[0].Dec("price"), sale.GetProperty("sale").Dec("total"));
    }

    [DatabaseFact]
    public async Task Branch_prices_apply_unless_pricing_is_uniform()
    {
        var product = await ProductWithBranchPriceAsync();

        Assert.Equal((12000m, 12000m), await PricesAsync(product));     // setting missing = false: the branch price

        var setting = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO settings (id, branch_id, setting_key, setting_value) VALUES (@setting, NULL, 'pricing.uniform', 'true')",
            new { setting });
        try
        {
            Assert.Equal((10000m, 10000m), await PricesAsync(product));   // uniform: every branch at the default price
        }
        finally
        {
            await ExecAsync("DELETE FROM settings WHERE id = @setting", new { setting });
        }
    }
}
