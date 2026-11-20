using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>
/// In-memory Node Service pointed at pos_test as node TK01, sharing the fixture's signing key.
/// The per-request user check is uncached by default so tests see database changes at once.
/// </summary>
public sealed class NodeServiceFactory(DatabaseFixture db, int userCacheSeconds = 0) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pos"] = db.ConnectionString,
            ["Database:MigrationsPath"] = DatabaseFixture.MigrationsPath,
            ["Node:Code"] = "TK01",
            ["Auth:KeyPath"] = db.KeyPath,
            ["Auth:TokenLifetimeHours"] = "12",
            ["Auth:UserCacheSeconds"] = userCacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
}
