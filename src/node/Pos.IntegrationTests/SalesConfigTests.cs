using System.Net;
using System.Text.Json;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

/// <summary>GET /api/v1/sales/config: what the payment dialog needs to show the same totals as the server.</summary>
[Collection(DatabaseCollection.Name)]
public class SalesConfigTests(DatabaseFixture db) : IAsyncLifetime
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

    [DatabaseFact]
    public async Task Config_lists_payment_methods_like_rms2_and_rounding_settings()
    {
        var (status, body) = await _client.GetAsync("/api/v1/sales/config", db.CashierToken());

        Assert.Equal(HttpStatusCode.OK, status);
        var methods = body.GetProperty("payment_methods").EnumerateArray().ToList();
        Assert.Equal(["CASH", "DEBIT", "QRIS", "TRANSFER", "CREDIT"], methods.Select(m => m.Str("code")));
        Assert.Equal(["TUNAI", "DEBIT", "QRIS", "TRANSFER", "KREDIT"], methods.Select(m => m.Str("name")));
        Assert.True(methods[0].GetProperty("is_cash").GetBoolean());
        Assert.True(methods[4].GetProperty("is_credit").GetBoolean());

        Assert.Equal(100m, body.Dec("cash_rounding_step"));
        Assert.Equal("nearest", body.Str("cash_rounding_mode"));
        Assert.Equal(0, body.GetProperty("currency_decimals").GetInt32());
    }

    [DatabaseFact]
    public async Task Discount_limit_comes_from_the_users_permissions()
    {
        var limited = db.TokenFor(db.Seed.CashierId, "kasir1", ["sale.create", "discount.max:10"]);
        var none = db.CashierToken();
        var unlimited = db.TokenFor(db.Seed.AdminId, "admin", ["*"]);

        var (_, ten) = await _client.GetAsync("/api/v1/sales/config", limited);
        var (_, zero) = await _client.GetAsync("/api/v1/sales/config", none);
        var (_, all) = await _client.GetAsync("/api/v1/sales/config", unlimited);

        // the seeded cashier role has no discount.max, so the token's 10% is not "still granted"
        Assert.Equal(0m, ten.Dec("discount_max_percent"));
        Assert.Equal(0m, zero.Dec("discount_max_percent"));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("discount_max_percent").ValueKind);
    }

    [DatabaseFact]
    public async Task Config_needs_login()
    {
        var (status, _) = await _client.GetAsync("/api/v1/sales/config", token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
