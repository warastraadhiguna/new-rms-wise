using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>In-memory Node Service pointed at pos_test as node TK01.</summary>
public sealed class NodeServiceFactory(DatabaseFixture db) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pos"] = db.ConnectionString,
            ["Database:MigrationsPath"] = DatabaseFixture.MigrationsPath,
            ["Node:Code"] = "TK01",
        }));
}
