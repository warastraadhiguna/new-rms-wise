namespace Pos.NodeService.Sync;

/// <summary>What the sync agent last did, for /health and the cashier's status bar. Thread-safe snapshot.</summary>
public sealed class SyncStatus
{
    private readonly Lock _gate = new();
    private Snapshot _current = new("not_started", null, null, null, null, 0, 0, 0, null, 0);

    /// <param name="State">not_configured | not_started | ok | offline | refused | error</param>
    public sealed record Snapshot(
        string State, DateTime? LastSuccessAt, DateTime? LastAttemptAt, DateTime? LastPushAt, DateTime? LastPullAt,
        int PendingOutbox, int FailedOutbox, long MasterVersion, string? LastError, long ClockSkewMs);

    public Snapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_gate) _current = change(_current);
    }
}
