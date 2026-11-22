namespace Pos.Shared.Sync;

/// <summary>Status bar text, e.g. "Sinkron: 2 menit lalu · 0 tertunda", and whether it should be red.</summary>
public sealed record SyncFreshnessText(string Text, bool IsAlert);

/// <summary>
/// How fresh the node's sync with HQ is. Red (alert) when the last success is older than <c>sync.alert_minutes</c>,
/// when it never succeeded, or when documents were refused for good ("gagal"). Sync switched off is not an alert.
/// Times are UTC from the Node Service clock (never the client PC's).
/// </summary>
public static class SyncFreshness
{
    public const int DefaultAlertMinutes = 15;

    public static SyncFreshnessText Describe(bool configured, DateTime? lastSuccessUtc, DateTime nowUtc, int pending, int failed, int alertMinutes)
    {
        if (!configured)
            return new("Sinkron: tidak aktif", false);

        var age = lastSuccessUtc is { } last ? nowUtc - last : (TimeSpan?)null;
        var when = age switch
        {
            null => "belum pernah",
            { TotalMinutes: < 1 } => "baru saja",
            { TotalMinutes: < 60 } a => $"{(int)a.TotalMinutes} menit lalu",
            { TotalHours: < 24 } a => $"{(int)a.TotalHours} jam lalu",
            { } a => $"{(int)a.TotalDays} hari lalu",
        };
        var text = $"Sinkron: {when} · {Math.Max(0, pending)} tertunda" + (failed > 0 ? $" · {failed} gagal" : "");
        var stale = age is null || age.Value > TimeSpan.FromMinutes(Math.Max(1, alertMinutes));
        return new(text, stale || failed > 0);
    }
}
