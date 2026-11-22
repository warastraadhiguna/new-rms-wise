using System.Globalization;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;

namespace Pos.NodeService.Sync;

/// <summary>
/// One sync cycle: push the outbox, pull master data until HQ has nothing newer, then a heartbeat. Without internet
/// the cycle fails with <see cref="SyncUnavailableException"/> and nothing is lost: selling goes on locally, the
/// outbox waits, the cursor stays — the next cycle catches up. Master data takes effect at once (every request
/// reads settings and prices from the database; the cached node row is reloaded when nodes or branches changed).
/// </summary>
public sealed class SyncAgent(
    DbConnectionFactory connections, HqSyncClient client, MasterDataApplier applier, OutboxPusher pusher,
    NodeIdentity nodeIdentity, DatabaseMigrator migrator, SyncStatus status, SyncOptions options, TimeProvider clock,
    ILogger<SyncAgent> logger)
{
    public bool Configured => options.Enabled && options.HasHq;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        if (!Configured)
        {
            status.Update(s => s with { State = "not_configured", LastError = null });
            return;
        }
        status.Update(s => s with { LastAttemptAt = started });

        try
        {
            var sent = await pusher.PushAllAsync(ct);
            status.Update(s => s with { LastPushAt = clock.GetUtcNow().UtcDateTime });

            var applied = await PullAllAsync(ct);
            status.Update(s => s with { LastPullAt = clock.GetUtcNow().UtcDateTime });

            var (pending, failed) = await pusher.CountsAsync(ct);
            var master = await MasterVersionAsync(ct);
            var reply = await client.HeartbeatAsync(new HeartbeatReport(OutboxPusher.AppVersion,
                migrator.GetAppliedScripts().Order(StringComparer.Ordinal).LastOrDefault(), master, pending, failed,
                clock.GetUtcNow().UtcDateTime), ct);

            var done = clock.GetUtcNow().UtcDateTime;
            await using (var c = await connections.OpenAsync(ct))
                await SyncState.SetAsync(c, null, SyncState.LastSuccessAt, done.ToString("O", CultureInfo.InvariantCulture), ct);
            status.Update(s => s with
            {
                State = "ok", LastSuccessAt = done, PendingOutbox = pending, FailedOutbox = failed, MasterVersion = master,
                LastError = null, ClockSkewMs = reply.ClockSkewMs,
            });
            if (sent > 0 || applied > 0)
                logger.LogInformation("Sync: {Sent} document(s) sent, {Applied} master row(s) applied, version {Version}.", sent, applied, master);
        }
        catch (SyncUnavailableException ex)
        {
            await RefreshCountsAsync(ct);
            status.Update(s => s with { State = "offline", LastError = ex.Message });
            throw;
        }
        catch (SyncRefusedException ex)
        {
            await RefreshCountsAsync(ct);
            status.Update(s => s with { State = "refused", LastError = $"{ex.Code}: {ex.Message}" });
            throw;
        }
    }

    /// <returns>Master rows applied.</returns>
    public async Task<int> PullAllAsync(CancellationToken ct)
    {
        var applied = 0;
        var since = await MasterVersionAsync(ct);
        while (true)
        {
            var page = await client.PullAsync(since, Math.Clamp(options.PullLimit, 1, 1000), ct);
            if (page.Changes.Count > 0 || page.NextSince != since)
            {
                var tables = await applier.ApplyAsync(page, ct);
                applied += page.Changes.Count;
                if (tables.Contains("nodes") || tables.Contains("branches"))
                    nodeIdentity.Reset();
            }
            if (page.NextSince < since)
                throw new InvalidOperationException($"HQ moved the cursor back ({since} → {page.NextSince}).");
            since = page.NextSince;
            if (!page.HasMore)
                return applied;
        }
    }

    public async Task<long> MasterVersionAsync(CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(ct);
        var value = await SyncState.GetAsync(c, null, SyncState.MasterVersion, ct);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>On start: last success and counts from the database, so the status bar is right before the first cycle.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(ct);
        var last = await SyncState.GetAsync(c, null, SyncState.LastSuccessAt, ct);
        var lastAt = DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : (DateTime?)null;
        var master = await MasterVersionAsync(ct);
        status.Update(s => s with { LastSuccessAt = lastAt, MasterVersion = master, State = Configured ? s.State : "not_configured" });
        await RefreshCountsAsync(ct);
    }

    private async Task RefreshCountsAsync(CancellationToken ct)
    {
        try
        {
            var (pending, failed) = await pusher.CountsAsync(ct);
            status.Update(s => s with { PendingOutbox = pending, FailedOutbox = failed });
        }
        catch (Exception ex) when (ex is MySqlConnector.MySqlException or InvalidOperationException)
        {
            // the database is down as well: keep the last counts
        }
    }
}
