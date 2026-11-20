using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Api;
using Pos.NodeService.Auth;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;

namespace Pos.IntegrationTests;

/// <summary>Lockout timing with a controllable clock.</summary>
[Collection(DatabaseCollection.Name)]
public class AuthServiceTests(DatabaseFixture db)
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [DatabaseFact]
    public async Task Lock_ends_after_five_minutes()
    {
        var name = "u" + Guid.NewGuid().ToString("N")[..12];
        await db.CreateUserAsync(name, "secret-1", roles: ("""["sale.create"]""", db.NodeId));
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var auth = new AuthService(new TransactionRunner(db.Factory), new NodeIdentity(db.Factory, "TK01"), db.Tokens, clock);

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ApiException>(() => auth.LoginWithPasswordAsync(new LoginRequest(name, "nope")));

        clock.Now = clock.Now.AddMinutes(4).AddSeconds(50);
        var locked = await Assert.ThrowsAsync<ApiException>(() => auth.LoginWithPasswordAsync(new LoginRequest(name, "secret-1")));
        Assert.Equal("account_locked", locked.Code);

        clock.Now = clock.Now.AddSeconds(11);
        var response = await auth.LoginWithPasswordAsync(new LoginRequest(name, "secret-1"));
        Assert.False(string.IsNullOrEmpty(response.AccessToken));
    }
}
