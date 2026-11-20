using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Auth;
using Pos.Shared.Auth;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class AuthApiTests(DatabaseFixture db) : IAsyncLifetime
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

    private Task<(HttpStatusCode Status, JsonElement Body)> Login(string username, string password) =>
        _client.PostAsync("/api/v1/auth/login", new { Username = username, Password = password });

    private Task<(HttpStatusCode Status, JsonElement Body)> Pin(string username, string pin) =>
        _client.PostAsync("/api/v1/auth/pin", new { Username = username, Pin = pin });

    private async Task<(HttpStatusCode Status, JsonElement Body)> OpenShift(string? token, Guid? device = null, object? body = null) =>
        await _client.PostAsync("/api/v1/shifts/open", body ?? new { OpeningCash = 0m }, token, device ?? await db.CreateDeviceAsync());

    // ---- login ---------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Correct_password_returns_a_token_that_opens_the_api()
    {
        var name = NewName();
        var userId = await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, db.NodeId));

        var (status, body) = await Login(name, "secret-1");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Bearer", body.Str("token_type"));
        Assert.Equal(userId, body.GetProperty("user").Id());
        Assert.Equal(["sale.create", "shift.close", "shift.open"],
            body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        var expiresAt = DateTime.Parse(body.Str("expires_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(expiresAt, DateTime.UtcNow.AddHours(11.9), DateTime.UtcNow.AddHours(12.1));

        var (shiftStatus, shift) = await OpenShift(body.Str("access_token"));
        Assert.Equal(HttpStatusCode.Created, shiftStatus);
        Assert.Equal(userId, shift.Id("cashier_id"));
    }

    [DatabaseFact]
    public async Task Seeded_cashier_can_log_in_with_password_and_pin()
    {
        Assert.Equal(HttpStatusCode.OK, (await Login("kasir1", "kasir123")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Pin("kasir1", "123456")).Status);
    }

    [DatabaseFact]
    public async Task Wrong_password_and_unknown_user_look_the_same()
    {
        var name = NewName();
        await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, db.NodeId));

        var wrongPassword = await Login(name, "nope");
        var unknownUser = await Login(NewName(), "secret-1");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.Status);
        Assert.Equal("invalid_credentials", wrongPassword.Body.Str("code"));
        Assert.Equal(wrongPassword.Body.Str("code"), unknownUser.Body.Str("code"));
        Assert.Equal(wrongPassword.Body.Str("detail"), unknownUser.Body.Str("detail"));
    }

    [DatabaseFact]
    public async Task Inactive_or_deleted_user_cannot_log_in()
    {
        var inactive = NewName();
        var deleted = NewName();
        await db.CreateUserAsync(inactive, "secret-1", pin: "1234", isActive: false, roles: (CashierRole, db.NodeId));
        await db.CreateUserAsync(deleted, "secret-1", pin: "1234", deleted: true, roles: (CashierRole, db.NodeId));

        Assert.Equal("invalid_credentials", (await Login(inactive, "secret-1")).Body.Str("code"));
        Assert.Equal("invalid_credentials", (await Login(deleted, "secret-1")).Body.Str("code"));
        Assert.Equal("invalid_credentials", (await Pin(inactive, "1234")).Body.Str("code"));
    }

    [DatabaseFact]
    public async Task User_with_rights_only_at_another_node_is_refused()
    {
        var otherNode = await db.CreateOtherNodeAsync();
        var name = NewName();
        await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, otherNode));

        var (status, body) = await Login(name, "secret-1");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("no_access_to_node", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Role_for_all_nodes_counts()
    {
        var name = NewName();
        await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, null));

        Assert.Equal(HttpStatusCode.OK, (await Login(name, "secret-1")).Status);
    }

    [DatabaseFact]
    public async Task Pin_login_and_wrong_or_missing_pin()
    {
        var withPin = NewName();
        var withoutPin = NewName();
        await db.CreateUserAsync(withPin, "secret-1", pin: "4321", roles: (CashierRole, db.NodeId));
        await db.CreateUserAsync(withoutPin, "secret-1", roles: (CashierRole, db.NodeId));

        Assert.Equal(HttpStatusCode.OK, (await Pin(withPin, "4321")).Status);
        Assert.Equal("invalid_credentials", (await Pin(withPin, "0000")).Body.Str("code"));
        Assert.Equal("invalid_credentials", (await Pin(withoutPin, "4321")).Body.Str("code"));
    }

    [DatabaseFact]
    public async Task Five_failures_lock_the_login_name_even_for_the_right_password()
    {
        var name = NewName();
        await db.CreateUserAsync(name, "secret-1", pin: "4321", roles: (CashierRole, db.NodeId));

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(name, "nope")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Pin(name, "0000")).Status);   // PIN failures count too

        var (status, body) = await Login(name, "secret-1");

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("account_locked", body.Str("code"));
        Assert.InRange(body.GetProperty("retry_after_seconds").GetInt32(), 1, 300);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Pin(name, "4321")).Status);
    }

    [DatabaseFact]
    public async Task Unknown_login_name_locks_the_same_way()
    {
        var name = NewName();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(name, "nope")).Status);

        Assert.Equal("account_locked", (await Login(name, "nope")).Body.Str("code"));
    }

    [DatabaseFact]
    public async Task Success_resets_the_failure_count()
    {
        var name = NewName();
        await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, db.NodeId));

        for (var i = 0; i < 4; i++)
            await Login(name, "nope");
        Assert.Equal(HttpStatusCode.OK, (await Login(name, "secret-1")).Status);
        for (var i = 0; i < 4; i++)
            await Login(name, "nope");

        Assert.Equal(HttpStatusCode.OK, (await Login(name, "secret-1")).Status);
    }

    // ---- tokens --------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Missing_or_garbage_token_is_401()
    {
        var none = await OpenShift(token: null);
        var garbage = await OpenShift(token: "not-a-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.Equal("unauthorized", none.Body.Str("code"));
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.Status);
    }

    [DatabaseFact]
    public async Task Expired_token_is_401()
    {
        var token = db.TokenFor(db.Seed.CashierId, now: DateTime.UtcNow.AddHours(-13));

        var (status, body) = await OpenShift(token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("token_expired", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Token_signed_with_another_key_is_401()
    {
        var otherKey = Path.Combine(Path.GetTempPath(), "pos-test-keys", Guid.NewGuid().ToString("N"), "other.key");
        var foreign = new TokenService(SigningKeyStore.LoadOrCreate(otherKey), "TK01", TimeSpan.FromHours(12))
            .Issue(db.Seed.CashierId, "kasir1", DatabaseFixture.CashierPermissions, DateTime.UtcNow).Token;

        Assert.Equal(HttpStatusCode.Unauthorized, (await OpenShift(foreign)).Status);
        Directory.Delete(Path.GetDirectoryName(otherKey)!, recursive: true);
    }

    [DatabaseFact]
    public async Task Token_for_another_node_is_401()
    {
        var foreignNode = new TokenService(SigningKeyStore.LoadOrCreate(db.KeyPath), "TK99", TimeSpan.FromHours(12))
            .Issue(db.Seed.CashierId, "kasir1", DatabaseFixture.CashierPermissions, DateTime.UtcNow).Token;

        Assert.Equal(HttpStatusCode.Unauthorized, (await OpenShift(foreignNode)).Status);
    }

    [DatabaseFact]
    public async Task Sales_without_token_is_401_and_without_permission_is_403()
    {
        var device = await db.CreateDeviceAsync();
        var sale = new { Id = Guid.CreateVersion7(), Lines = Array.Empty<object>(), Payments = Array.Empty<object>() };

        var anonymous = await _client.PostAsync("/api/v1/sales", sale, token: null, device: device);
        var shiftOnly = await _client.PostAsync("/api/v1/sales", sale,
            db.TokenFor(db.Seed.CashierId, permissions: [Permissions.ShiftOpen]), device);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal(HttpStatusCode.Forbidden, shiftOnly.Status);
        Assert.Equal("forbidden", shiftOnly.Body.Str("code"));
    }

    [DatabaseFact]
    public async Task Wildcard_permission_allows_everything()
    {
        var (status, _) = await OpenShift(db.TokenFor(db.Seed.AdminId, "admin", [Permissions.All]));
        Assert.Equal(HttpStatusCode.Created, status);
    }

    // ---- device header and identity from token -------------------------------------------------

    [DatabaseFact]
    public async Task Device_header_is_required_and_must_be_an_active_device_of_this_node()
    {
        var token = db.CashierToken();
        var otherNodeDevice = await db.CreateDeviceAsync(nodeId: await db.CreateOtherNodeAsync());

        var missing = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 0m }, token, device: null);
        var unknown = await OpenShift(token, Guid.CreateVersion7());
        var pending = await OpenShift(token, await db.CreateDeviceAsync(status: "PENDING"));
        var foreign = await OpenShift(token, otherNodeDevice);

        Assert.Equal((HttpStatusCode.BadRequest, "device_required"), (missing.Status, missing.Body.Str("code")));
        Assert.All(new[] { unknown, pending, foreign }, r =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("device_not_allowed", r.Body.Str("code"));
        });
    }

    [DatabaseFact]
    public async Task Cashier_id_in_the_request_is_ignored()
    {
        var name = NewName();
        var userId = await db.CreateUserAsync(name, "secret-1", roles: (CashierRole, db.NodeId));
        var token = (await Login(name, "secret-1")).Body.Str("access_token");
        var device = await db.CreateDeviceAsync();

        var (status, shift) = await OpenShift(token, device,
            new { OpeningCash = 50000m, CashierId = db.Seed.AdminId, DeviceId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(userId, shift.Id("cashier_id"));
        Assert.Equal(device, shift.Id("device_id"));

        var product = await db.CreateSellableProductAsync(1000m);
        var (saleStatus, sale) = await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CashierId = db.Seed.AdminId,
            CreatedBy = db.Seed.AdminId,
            Lines = new[] { new { ProductUnitId = product.ProductUnitId, Qty = 1m } },
            Payments = new[] { new { Method = "CASH", Amount = 1000m } },
        }, token, device);

        Assert.Equal(HttpStatusCode.Created, saleStatus);
        Assert.Equal(userId, sale.GetProperty("sale").Id("created_by"));
    }

    // ---- key storage ---------------------------------------------------------------------------

    [Fact]
    public void Signing_key_is_created_once_and_not_stored_in_plain_text()
    {
        var path = Path.Combine(Path.GetTempPath(), "pos-test-keys", Guid.NewGuid().ToString("N"), "jwt-signing.key");
        try
        {
            var first = SigningKeyStore.LoadOrCreate(path);
            var second = SigningKeyStore.LoadOrCreate(path);

            Assert.Equal(64, first.Length);
            Assert.Equal(first, second);
            var onDisk = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain(Convert.ToHexString(first), Convert.ToHexString(onDisk));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Passwords_are_bcrypt_and_accept_laravel_hashes()
    {
        var hash = PasswordHasher.Hash("secret", workFactor: 4);

        Assert.StartsWith("$2y$04$", hash);
        Assert.True(PasswordHasher.Verify("secret", hash));
        Assert.False(PasswordHasher.Verify("Secret", hash));
        Assert.False(PasswordHasher.Verify("secret", null));
        Assert.False(PasswordHasher.Verify("secret", "!dev-no-login"));
        // Laravel's well-known default factory hash of "password"
        Assert.True(PasswordHasher.Verify("password", "$2y$10$92IXUNpkjO0rOQ5byMi.Ye4oKoEa3Ro9llC/.og/at2.uheWG/igi"));
    }
}
