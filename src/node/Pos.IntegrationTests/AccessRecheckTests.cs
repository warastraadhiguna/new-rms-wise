using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Auth;

namespace Pos.IntegrationTests;

/// <summary>Per-request user re-check and shift ownership for sales.</summary>
[Collection(DatabaseCollection.Name)]
public class AccessRecheckTests(DatabaseFixture db) : IAsyncLifetime
{
    private const string CashierRole = """["shift.open","shift.close","sale.create"]""";

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

    private static string NewName() => "u" + Guid.NewGuid().ToString("N")[..12];

    private async Task<(Guid UserId, string Token)> LoginNewCashierAsync(string? pin = null, string roles = CashierRole)
    {
        var name = NewName();
        var userId = await db.CreateUserAsync(name, "secret-1", pin, roles: (roles, db.NodeId));
        var (status, body) = await _client.PostAsync("/api/v1/auth/login", new { Username = name, Password = "secret-1" });
        Assert.Equal(HttpStatusCode.OK, status);
        return (userId, body.Str("access_token"));
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> OpenShift(HttpClient client, string token, Guid device) =>
        client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token, device);

    private Task<(HttpStatusCode Status, JsonElement Body)> Sell(string token, Guid device, TestProduct product) =>
        _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            Lines = new[] { new { ProductUnitId = product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = 1000m } },
        }, token, device);

    // ---- 1. re-check on every request ------------------------------------------------------------

    [DatabaseFact]
    public async Task Deactivated_user_is_rejected_although_the_token_is_still_valid()
    {
        var (userId, token) = await LoginNewCashierAsync();
        Assert.Equal(HttpStatusCode.Created, (await OpenShift(_client, token, await db.CreateDeviceAsync())).Status);

        await ExecAsync("UPDATE users SET is_active = 0 WHERE id = @userId", new { userId });
        var (status, body) = await OpenShift(_client, token, await db.CreateDeviceAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("user_inactive", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Deleted_user_is_rejected()
    {
        var (userId, token) = await LoginNewCashierAsync();

        await ExecAsync("UPDATE users SET deleted_at = UTC_TIMESTAMP(3) WHERE id = @userId", new { userId });
        var (status, body) = await OpenShift(_client, token, await db.CreateDeviceAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("user_inactive", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Revoked_permission_is_refused_immediately()
    {
        var (userId, token) = await LoginNewCashierAsync();

        await ExecAsync(
            "UPDATE roles r JOIN user_roles ur ON ur.role_id = r.id SET r.permissions = '[\"shift.close\"]' WHERE ur.user_id = @userId",
            new { userId });
        var (status, body) = await OpenShift(_client, token, await db.CreateDeviceAsync());

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("forbidden", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Removed_role_at_this_node_is_refused()
    {
        var (userId, token) = await LoginNewCashierAsync();

        await ExecAsync("UPDATE user_roles SET deleted_at = UTC_TIMESTAMP(3) WHERE user_id = @userId", new { userId });

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenShift(_client, token, await db.CreateDeviceAsync())).Status);
    }

    [DatabaseFact]
    public async Task Newly_granted_permission_needs_a_new_login()
    {
        var (userId, token) = await LoginNewCashierAsync(roles: """["shift.close"]""");

        await ExecAsync(
            "UPDATE roles r JOIN user_roles ur ON ur.role_id = r.id SET r.permissions = '[\"shift.open\"]' WHERE ur.user_id = @userId",
            new { userId });

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenShift(_client, token, await db.CreateDeviceAsync())).Status);
    }

    [DatabaseFact]
    public async Task With_the_cache_a_change_shows_up_after_at_most_the_cache_time()
    {
        await using var cached = new NodeServiceFactory(db, userCacheSeconds: 30);
        using var client = cached.CreateClient();
        var (userId, token) = await LoginNewCashierAsync();
        Assert.Equal(HttpStatusCode.Created, (await OpenShift(client, token, await db.CreateDeviceAsync())).Status);

        await ExecAsync("UPDATE users SET is_active = 0 WHERE id = @userId", new { userId });

        // still within the 30 s cache window: the earlier check is reused
        Assert.Equal(HttpStatusCode.Created, (await OpenShift(client, token, await db.CreateDeviceAsync())).Status);
    }

    // ---- 2. shift ownership ----------------------------------------------------------------------

    [DatabaseFact]
    public async Task Only_the_shift_owner_can_sell_on_it()
    {
        var device = await db.CreateDeviceAsync();
        var product = await db.CreateSellableProductAsync(1000m);
        var (_, owner) = await LoginNewCashierAsync();
        var (_, other) = await LoginNewCashierAsync();
        await OpenShift(_client, owner, device);

        var (ownerStatus, _) = await Sell(owner, device, product);
        var (otherStatus, body) = await Sell(other, device, product);

        Assert.Equal(HttpStatusCode.Created, ownerStatus);
        Assert.Equal(HttpStatusCode.Forbidden, otherStatus);
        Assert.Equal("not_shift_owner", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Shift_share_permission_allows_selling_on_someone_elses_shift()
    {
        var device = await db.CreateDeviceAsync();
        var product = await db.CreateSellableProductAsync(1000m);
        var (_, owner) = await LoginNewCashierAsync();
        var (helperId, helper) = await LoginNewCashierAsync(roles: """["sale.create","shift.share"]""");
        await OpenShift(_client, owner, device);

        var (status, sale) = await Sell(helper, device, product);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(helperId, sale.GetProperty("sale").Id("created_by"));
    }

    [DatabaseFact]
    public async Task Wildcard_includes_shift_share()
    {
        var device = await db.CreateDeviceAsync();
        var product = await db.CreateSellableProductAsync(1000m);
        var (_, owner) = await LoginNewCashierAsync();
        await OpenShift(_client, owner, device);

        var (status, _) = await Sell(db.TokenFor(db.Seed.AdminId, "admin", [Permissions.All]), device, product);

        Assert.Equal(HttpStatusCode.Created, status);
    }

    [DatabaseFact]
    public async Task Cashier_change_by_pin_closes_the_old_shift_and_opens_an_own_one()
    {
        var device = await db.CreateDeviceAsync();
        var product = await db.CreateSellableProductAsync(1000m);
        var (_, first) = await LoginNewCashierAsync();
        var secondName = NewName();
        var secondId = await db.CreateUserAsync(secondName, "secret-1", pin: "2468", roles: (CashierRole, db.NodeId));
        var (_, opened) = await OpenShift(_client, first, device);

        var second = (await _client.PostAsync("/api/v1/auth/pin", new { Username = secondName, Pin = "2468" })).Body.Str("access_token");
        Assert.Equal(HttpStatusCode.Forbidden, (await Sell(second, device, product)).Status);

        await _client.PostAsync("/api/v1/shifts/close", new { ShiftId = opened.Id(), CountedCash = 0m }, first, device);
        var (openStatus, own) = await OpenShift(_client, second, device);
        var (saleStatus, sale) = await Sell(second, device, product);

        Assert.Equal(HttpStatusCode.Created, openStatus);
        Assert.Equal(secondId, own.Id("cashier_id"));
        Assert.Equal(HttpStatusCode.Created, saleStatus);
        Assert.Equal(own.Id(), sale.GetProperty("sale").Id("shift_id"));
    }
}
