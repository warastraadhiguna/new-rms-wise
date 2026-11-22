namespace Pos.NodeService.Sync;

/// <summary>
/// Runs <see cref="SyncAgent"/> in the background: every Sync:IntervalSeconds while HQ answers; after a failure the
/// pause doubles up to Sync:MaxBackoffSeconds (no internet for hours costs nothing), and the first success after that
/// catches up everything that waited.
/// </summary>
public sealed class SyncWorker(SyncAgent agent, SyncOptions options, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Worker)
            return;
        try
        {
            await agent.LoadAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Sync status could not be loaded: {Error}", ex.Message);
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds));
            try
            {
                await agent.RunOnceAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var level = ex is SyncUnavailableException && failures > 1 ? LogLevel.Debug : LogLevel.Warning;
                logger.Log(level, "Sync failed ({Failures}x): {Error}", failures, ex.Message);
                interval = Backoff(interval, failures, TimeSpan.FromSeconds(Math.Max(1, options.MaxBackoffSeconds)));
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>interval × 2^(failures-1), at most <paramref name="max"/>.</summary>
    public static TimeSpan Backoff(TimeSpan interval, int failures, TimeSpan max)
    {
        var factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 20));
        var seconds = Math.Min(max.TotalSeconds, interval.TotalSeconds * factor);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }
}
