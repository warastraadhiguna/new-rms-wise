using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class HealthEndpointTests(DatabaseFixture db)
{
    [DatabaseFact]
    public async Task Health_reports_database_and_latest_migration()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Pos"] = db.ConnectionString,
                ["Database:MigrationsPath"] = DatabaseFixture.MigrationsPath,
            })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var database = json.RootElement.GetProperty("database");
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ok", database.GetProperty("status").GetString());
        Assert.Equal("0001", database.GetProperty("migrationVersion").GetString());
        Assert.Equal("0001_initial_schema.sql", database.GetProperty("migrationScript").GetString());
    }
}
