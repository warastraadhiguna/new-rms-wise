using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>
/// In-memory Node Service pointed at pos_test as node TK01, sharing the fixture's signing key.
/// The per-request user check is uncached by default so tests see database changes at once.
/// Sync is off unless <paramref name="settings"/> sets Sync:HqUrl; its background worker never runs in tests.
/// </summary>
public sealed class NodeServiceFactory(DatabaseFixture db, int userCacheSeconds = 0, IDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Pos"] = db.ConnectionString,
                ["Database:MigrationsPath"] = DatabaseFixture.MigrationsPath,
                ["Node:Code"] = "TK01",
                ["Auth:KeyPath"] = db.KeyPath,
                ["Auth:TokenLifetimeHours"] = "12",
                ["Auth:UserCacheSeconds"] = userCacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Sync:Worker"] = "false",
                ["Sync:TokenPath"] = Path.Combine(Path.GetDirectoryName(db.KeyPath)!, "sync-token.dat"),
            };
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                values[key] = value;
            config.AddInMemoryCollection(values);
        });
}
