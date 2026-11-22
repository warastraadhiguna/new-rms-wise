using System.Net;
using System.Text.Json;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class HealthEndpointTests(DatabaseFixture db)
{
    [DatabaseFact]
    public async Task Health_reports_database_server_migration_and_node()
    {
        await using var factory = new NodeServiceFactory(db);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var database = root.GetProperty("database");
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("ok", database.GetProperty("status").GetString());
        Assert.Equal("0010", database.GetProperty("migration_version").GetString());
        Assert.Equal("0010_innodb_journal.sql", database.GetProperty("migration_script").GetString());
        Assert.Equal("MySQL", database.GetProperty("server_product").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", database.GetProperty("server_version").GetString());
        Assert.Equal("TK01", root.GetProperty("node").GetProperty("code").GetString());
        Assert.Equal("ok", root.GetProperty("node").GetProperty("status").GetString());
    }
}
