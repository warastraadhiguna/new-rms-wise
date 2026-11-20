using Pos.NodeService.Data;
using Pos.NodeService.Nodes;

namespace Pos.NodeService.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app, ServerVersionCheckResult server)
    {
        app.MapGet("/health", async (DbConnectionFactory connections, DatabaseMigrator migrator, NodeIdentity nodeIdentity, CancellationToken ct) =>
        {
            object database;
            var healthy = true;
            try
            {
                await using (await connections.OpenAsync(ct)) { }
                var latest = migrator.GetAppliedScripts().Order(StringComparer.Ordinal).LastOrDefault();
                database = new
                {
                    Status = "ok",
                    ServerProduct = server.Product,
                    ServerVersion = server.Version?.ToString(),
                    MigrationVersion = latest is null ? null : DatabaseMigrator.VersionOf(latest),
                    MigrationScript = latest,
                };
            }
            catch (Exception ex)
            {
                healthy = false;
                database = new { Status = "unavailable", Error = ex.Message };
            }

            object node;
            try
            {
                var info = await nodeIdentity.GetAsync(ct);
                node = new { Status = "ok", info.Code, info.Id, info.BranchId, info.TimeZone };
            }
            catch (Exception ex) when (ex is NodeNotFoundException || !healthy)
            {
                healthy = false;
                node = new { Status = "not_found", nodeIdentity.Code, Error = ex.Message };
            }

            var body = new { Status = healthy ? "ok" : "degraded", Utc = DateTime.UtcNow, Database = database, Node = node };
            return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }
}
