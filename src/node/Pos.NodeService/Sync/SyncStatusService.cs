using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Sync;

namespace Pos.NodeService.Sync;

public sealed record SyncStatusDto(
    bool Configured, bool Initialized, string State, DateTime? LastSuccessAt, DateTime? LastAttemptAt, int PendingOutbox,
    int FailedOutbox, long MasterVersion, int AlertMinutes, string? LastError, long ClockSkewMs, DateTime ServerTime,
    string Text, bool IsAlert);

/// <summary>GET /api/v1/sync/status and the "sync" part of /health. The alert limit is setting sync.alert_minutes (HQ).</summary>
public sealed class SyncStatusService(
    SyncStatus status, SyncAgent agent, NodeInitialization initialization, DbConnectionFactory connections,
    NodeIdentity nodeIdentity, TimeProvider clock)
{
    public const string AlertMinutesSetting = "sync.alert_minutes";

    public async Task<SyncStatusDto> GetAsync(CancellationToken ct)
    {
        var s = status.Current;
        var alertMinutes = SyncFreshness.DefaultAlertMinutes;
        var initialized = false;
        try
        {
            initialized = await initialization.IsInitializedAsync(ct);
            var node = await nodeIdentity.GetAsync(ct);
            await using var c = await connections.OpenAsync(ct);
            alertMinutes = await SettingsReader.GetIntAsync(c, null, AlertMinutesSetting, node.BranchId, SyncFreshness.DefaultAlertMinutes, ct);
        }
        catch (Exception ex) when (ex is NodeNotFoundException or MySqlConnector.MySqlException)
        {
            // not initialized or the database is down: defaults
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var text = SyncFreshness.Describe(agent.Configured, s.LastSuccessAt, now, s.PendingOutbox, s.FailedOutbox, alertMinutes);
        return new SyncStatusDto(agent.Configured, initialized, s.State, s.LastSuccessAt, s.LastAttemptAt, s.PendingOutbox, s.FailedOutbox,
            s.MasterVersion, alertMinutes, s.LastError, s.ClockSkewMs, now, text.Text, text.IsAlert);
    }
}
