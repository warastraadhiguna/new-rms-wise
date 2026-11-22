using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Dapper;
using Pos.NodeService.Data;

namespace Pos.NodeService.Sync;

/// <summary>
/// Sends the outbox to HQ in id order, a batch at a time. Per result: accepted / duplicate / stale = sent;
/// rejected with retry = try that document again later (backoff per document, the others go on);
/// rejected without retry = parked (failed_at), shown as "gagal" — it needs an admin. When HQ does not answer
/// nothing is marked: the same documents go again next time (HQ is idempotent per doc_id and row_version).
/// Rows written before format 2 (API DTO payloads) get their snapshot rebuilt from the stored rows.
/// </summary>
public sealed class OutboxPusher(DbConnectionFactory connections, HqSyncClient client, TimeProvider clock, SyncOptions options)
{
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private sealed record OutboxRow(long Id, string DocType, Guid DocId, int RowVersion, string Payload, DateTime CreatedAt, int Attempts);

    /// <returns>Documents marked as sent.</returns>
    public async Task<int> PushAllAsync(CancellationToken ct)
    {
        var sent = 0;
        while (true)
        {
            var (batchSent, batchSize) = await PushBatchAsync(ct);
            sent += batchSent;
            if (batchSize < Math.Max(1, options.PushBatchSize) || batchSent == 0)
                return sent;
        }
    }

    private async Task<(int Sent, int Size)> PushBatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        List<OutboxRow> rows;
        await using (var c = await connections.OpenAsync(ct))
        {
            rows = (await c.QueryAsync<OutboxRow>(new CommandDefinition(
                """
                SELECT id AS Id, doc_type AS DocType, doc_id AS DocId, row_version AS RowVersion, CAST(payload AS CHAR) AS Payload,
                       created_at AS CreatedAt, attempts AS Attempts
                FROM sync_outbox
                WHERE sent_at IS NULL AND failed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                ORDER BY id LIMIT @limit
                """, new { now, limit = Math.Max(1, options.PushBatchSize) }, cancellationToken: ct))).ToList();
        }
        if (rows.Count == 0)
            return (0, 0);

        var documents = new JsonArray();
        var parked = new List<(long Id, string Error)>();
        foreach (var row in rows)
        {
            var payload = JsonNode.Parse(row.Payload) as JsonObject;
            if (payload?["format"]?.GetValue<int>() != DocumentSnapshot.Format)
            {
                try
                {
                    await using var c = await connections.OpenAsync(ct);
                    payload = await DocumentSnapshot.ReadAsync(c, null, row.DocType, row.DocId, ct);
                }
                catch (InvalidOperationException ex)
                {
                    parked.Add((row.Id, ex.Message));
                    continue;
                }
            }
            documents.Add(new JsonObject
            {
                ["outbox_id"] = row.Id,
                ["doc_type"] = row.DocType,
                ["doc_id"] = row.DocId.ToString(),
                ["row_version"] = row.RowVersion,
                ["occurred_at"] = row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                ["app_version"] = AppVersion,
                ["payload"] = payload,
            });
        }

        var results = documents.Count == 0 ? [] : await client.PushAsync(documents, ct);

        var sent = 0;
        await using (var c = await connections.OpenAsync(ct))
        {
            now = clock.GetUtcNow().UtcDateTime;
            foreach (var (id, error) in parked)
                await Exec(c, "UPDATE sync_outbox SET failed_at = @now, last_error = @error WHERE id = @id", new { id, now, error }, ct);

            var attempts = rows.ToDictionary(r => r.Id, r => r.Attempts);
            foreach (var result in results)
            {
                if (!attempts.TryGetValue(result.OutboxId, out var tried))
                    continue;                                    // not one of ours (should not happen)
                if (result.Status is "accepted" or "duplicate" or "stale")
                {
                    await Exec(c, "UPDATE sync_outbox SET sent_at = @now, attempts = attempts + 1, last_error = NULL, next_attempt_at = NULL WHERE id = @id",
                        new { id = result.OutboxId, now }, ct);
                    sent++;
                }
                else if (result.Retry)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(3600, 10 * Math.Pow(2, Math.Min(tried, 10))));
                    await Exec(c, "UPDATE sync_outbox SET attempts = attempts + 1, last_error = @error, next_attempt_at = @next WHERE id = @id",
                        new { id = result.OutboxId, error = $"{result.Code}: {result.Message}", next = now + wait }, ct);
                }
                else
                {
                    await Exec(c, "UPDATE sync_outbox SET attempts = attempts + 1, failed_at = @now, last_error = @error WHERE id = @id",
                        new { id = result.OutboxId, now, error = $"{result.Code}: {result.Message}" }, ct);
                }
            }
        }
        return (sent, rows.Count);
    }

    public async Task<(int Pending, int Failed)> CountsAsync(CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(ct);
        return await c.QuerySingleAsync<(int, int)>(new CommandDefinition(
            """
            SELECT CAST(COALESCE(SUM(sent_at IS NULL AND failed_at IS NULL), 0) AS SIGNED),
                   CAST(COALESCE(SUM(failed_at IS NOT NULL), 0) AS SIGNED)
            FROM sync_outbox
            """, cancellationToken: ct));
    }

    private static Task Exec(MySqlConnector.MySqlConnection c, string sql, object args, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
}
