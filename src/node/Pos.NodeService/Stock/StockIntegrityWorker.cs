using Pos.NodeService.Nodes;
using Pos.Shared.Time;

namespace Pos.NodeService.Stock;

/// <summary>
/// Runs the stock integrity check at startup and every night at <c>StockIntegrity:DailyAt</c> ("HH:mm",
/// branch time, default 02:00). <c>StockIntegrity:RunOnStartup</c> (default true) and
/// <c>StockIntegrity:Enabled</c> (default true) switch the parts off. A failed check is logged and kept for
/// /health; the schedule continues.
/// </summary>
public sealed class StockIntegrityWorker(
    StockIntegrityService integrity,
    StockIntegrityStatus status,
    NodeIdentity nodeIdentity,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<StockIntegrityWorker> logger) : BackgroundService
{
    public static readonly TimeOnly DefaultDailyAt = new(2, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("StockIntegrity:Enabled", true))
        {
            logger.LogWarning("Stock integrity check is disabled (StockIntegrity:Enabled = false).");
            return;
        }

        var dailyAt = DailyAt();
        // Let the host finish starting first; the check reads the whole ledger.
        await Task.Yield();

        if (configuration.GetValue("StockIntegrity:RunOnStartup", true))
            await RunCheckAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var timeZone = await BranchTimeZoneAsync(stoppingToken);
            var now = clock.GetUtcNow().UtcDateTime;
            var next = DailySchedule.NextUtc(now, dailyAt, timeZone);
            logger.LogInformation("Next stock integrity check at {Next:u} ({Local} {TimeZone}).", next, dailyAt, timeZone.Id);

            try
            {
                await Task.Delay(next - now, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunCheckAsync(stoppingToken);
        }
    }

    private async Task RunCheckAsync(CancellationToken stoppingToken)
    {
        try
        {
            await integrity.CheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            status.SetError(ex.Message, clock.GetUtcNow().UtcDateTime);
            logger.LogError(ex, "Stock integrity check failed: {Error}", ex.Message);
        }
    }

    private TimeOnly DailyAt()
    {
        var text = configuration["StockIntegrity:DailyAt"];
        if (text is null)
            return DefaultDailyAt;
        if (DailySchedule.TryParseTime(text, out var time))
            return time;

        logger.LogError("StockIntegrity:DailyAt '{Value}' is not a time (HH:mm); using {Default}.", text, DefaultDailyAt);
        return DefaultDailyAt;
    }

    /// <summary>The branch timezone; the PC's own zone until the node row has arrived from HQ.</summary>
    private async Task<TimeZoneInfo> BranchTimeZoneAsync(CancellationToken ct)
    {
        try
        {
            var node = await nodeIdentity.GetAsync(ct);
            return TimeZoneInfo.FindSystemTimeZoneById(node.TimeZone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Branch timezone unavailable ({Error}); scheduling the stock check in the PC's timezone.", ex.Message);
            return TimeZoneInfo.Local;
        }
    }
}
