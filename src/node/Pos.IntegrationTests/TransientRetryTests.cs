using MySqlConnector;
using Pos.IntegrationTests.Infrastructure;
using Pos.NodeService.Data;

namespace Pos.IntegrationTests;

/// <summary>Retry policy with fake failures; no database needed.</summary>
public class TransientRetryTests
{
    private readonly ListLogger<TransientRetryTests> _log = new();
    private readonly List<TimeSpan> _delays = [];

    private TransientRetry Retry() => new(_log, (delay, _) =>
    {
        _delays.Add(delay);
        return Task.CompletedTask;
    });

    private static MySqlException Error(MySqlErrorCode code) => MySqlErrorFactory.Create(code);

    [Theory]
    [InlineData(MySqlErrorCode.LockDeadlock)]       // 1213
    [InlineData(MySqlErrorCode.LockWaitTimeout)]    // 1205
    public async Task Transient_error_is_retried_until_success(MySqlErrorCode code)
    {
        var attempts = 0;

        var result = await Retry().ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 3 ? throw Error(code) : Task.FromResult("done");
        }, "test");

        Assert.Equal("done", result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, _log.Warnings.Count);
        Assert.All(_log.Warnings, w => Assert.Contains(((int)code).ToString(), w));
    }

    [Fact]
    public async Task Gives_up_after_three_retries_and_rethrows()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<MySqlException>(() => Retry().ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw Error(MySqlErrorCode.LockDeadlock);
        }, "test"));

        Assert.Equal(MySqlErrorCode.LockDeadlock, ex.ErrorCode);
        Assert.Equal(1 + TransientRetry.MaxRetries, attempts);
        Assert.Equal(3, TransientRetry.MaxRetries);
        Assert.Equal(3, _log.Warnings.Count);
    }

    [Fact]
    public async Task Delays_grow_between_retries()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<MySqlException>(() => Retry().ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw Error(MySqlErrorCode.LockWaitTimeout);
        }, "test"));

        Assert.Equal(3, _delays.Count);
        Assert.True(_delays[0] > TimeSpan.Zero);
        Assert.True(_delays[1] > _delays[0]);
        Assert.True(_delays[2] > _delays[1]);
        Assert.True(_delays[2] < TimeSpan.FromSeconds(1), "delays stay short");
    }

    [Fact]
    public async Task Other_mysql_errors_are_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<MySqlException>(() => Retry().ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw Error(MySqlErrorCode.DuplicateKeyEntry);
        }, "test"));

        Assert.Equal(1, attempts);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task Non_mysql_errors_are_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry().ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, "test"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_stops_retrying()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TransientRetry(_log, (_, ct) =>
        {
            cts.Cancel();
            return Task.Delay(1, ct);
        }).ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw Error(MySqlErrorCode.LockDeadlock);
        }, "test", cts.Token));

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(MySqlErrorCode.LockDeadlock, true)]
    [InlineData(MySqlErrorCode.LockWaitTimeout, true)]
    [InlineData(MySqlErrorCode.DuplicateKeyEntry, false)]
    public void Transient_classification(MySqlErrorCode code, bool expected)
    {
        Assert.Equal(expected, TransientRetry.IsTransient(Error(code)));
    }
}
