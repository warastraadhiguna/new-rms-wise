using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.NodeService.Stock;
using Pos.NodeService.Sync;

namespace Pos.NodeService.Health;

public static class HealthEndpoints
{
    /// <summary>At most this many differing buckets are listed in /health (the log has up to 100).</summary>
    public const int MaxListedMismatches = 20;

    /// <summary>
    /// status: "ok"; "warning" (service works but the stock integrity check found differences or failed);
    /// "degraded" with HTTP 503 when the database or the node row is unavailable.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app, ServerVersionCheckResult server)
    {
        app.MapGet("/health", async (DbConnectionFactory connections, DatabaseMigrator migrator, NodeIdentity nodeIdentity,
            StockIntegrityStatus integrity, SyncStatusService sync, CancellationToken ct) =>
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

            var (stockStatus, stock) = StockIntegrity(integrity);
            var status = !healthy ? "degraded" : stockStatus is "warning" or "error" ? "warning" : "ok";

            object? syncBody = null;
            if (healthy)
            {
                var s = await sync.GetAsync(ct);
                syncBody = new
                {
                    s.State, s.Configured, s.Initialized, s.LastSuccessAt, s.LastAttemptAt, s.PendingOutbox, s.FailedOutbox,
                    s.MasterVersion, s.AlertMinutes, s.IsAlert, s.Text, s.LastError, s.ClockSkewMs,
                };
            }

            var body = new { Status = status, Utc = DateTime.UtcNow, Database = database, Node = node, StockIntegrity = stock, Sync = syncBody };
            return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }

    /// <returns>"pending" (not checked yet), "ok", "warning" (differences found) or "error" (the last check failed).</returns>
    private static (string Status, object Body) StockIntegrity(StockIntegrityStatus integrity)
    {
        var report = integrity.LastReport;
        var failedAfterReport = integrity.LastError is not null
            && (report is null || integrity.LastErrorAt > report.CheckedAt);
        var status = failedAfterReport ? "error"
            : report is null ? "pending"
            : report.IsConsistent ? "ok"
            : "warning";

        return (status, new
        {
            Status = status,
            CheckedAt = report?.CheckedAt,
            BalanceRows = report?.BalanceRows,
            MismatchCount = report?.MismatchCount,
            Mismatches = report?.Mismatches.Take(MaxListedMismatches).Select(m => new
            {
                m.LocationId, m.LocationCode, m.ProductId, m.Sku, m.ExpiryDate, m.BalanceQty, m.LedgerQty, m.Difference,
                m.BalanceRowExists,
            }),
            Error = failedAfterReport ? integrity.LastError : null,
            ErrorAt = failedAfterReport ? integrity.LastErrorAt : null,
        });
    }
}
