using MySqlConnector;

namespace Pos.NodeService.Data;

/// <summary>
/// Re-runs a whole database transaction after a deadlock (1213) or lock wait timeout (1205).
/// MySQL rolls the victim back, so repeating the complete unit of work is safe.
/// </summary>
public sealed class TransientRetry(ILogger logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int MaxRetries = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(50);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static bool IsTransient(Exception exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout };

    /// <param name="operation">Short name for the log, e.g. "stock posting".</param>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action, string operation, CancellationToken cancellationToken = default)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (MySqlException ex) when (retry < MaxRetries && IsTransient(ex))
            {
                var wait = DelayFor(retry);
                logger.LogWarning(
                    "{Operation} hit MySQL error {ErrorNumber} ({ErrorCode}); retry {Retry} of {MaxRetries} in {DelayMs} ms.",
                    operation, (int)ex.ErrorCode, ex.ErrorCode, retry + 1, MaxRetries, (int)wait.TotalMilliseconds);
                await _delay(wait, cancellationToken);
            }
        }
    }

    /// <summary>50 ms, 100 ms, 200 ms, each plus up to 50% jitter so parallel victims do not collide again.</summary>
    private static TimeSpan DelayFor(int retry)
    {
        var baseMs = BaseDelay.TotalMilliseconds * (1 << retry);
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.NextDouble() * baseMs / 2);
    }
}
