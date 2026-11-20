using Pos.NodeService.Data;

namespace Pos.NodeService.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (DbConnectionFactory connections, DatabaseMigrator migrator, CancellationToken ct) =>
        {
            object database;
            var healthy = true;
            try
            {
                await using (await connections.OpenAsync(ct)) { }
                var latest = migrator.GetAppliedScripts().Order(StringComparer.Ordinal).LastOrDefault();
                database = new
                {
                    status = "ok",
                    migrationVersion = latest is null ? null : DatabaseMigrator.VersionOf(latest),
                    migrationScript = latest,
                };
            }
            catch (Exception ex)
            {
                healthy = false;
                database = new { status = "unavailable", error = ex.Message };
            }

            var body = new { status = healthy ? "ok" : "degraded", utc = DateTime.UtcNow, database };
            return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }
}
