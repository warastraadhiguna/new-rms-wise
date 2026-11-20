using System.Data;
using MySqlConnector;
using Pos.NodeService.Data;
using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.NodeService.Stock;

/// <summary>One stock movement of a document line. <c>ExpiryDate</c> is used for inbound movements only.</summary>
public sealed record StockMovement(
    StockDocumentLine Line,
    StockDirection Direction,
    decimal BaseQty,
    DateOnly? ExpiryDate = null,
    bool AllowExpired = false);

/// <summary>
/// Posts stock movements: locks the affected balance rows, allocates with Pos.Shared, then writes
/// stock_ledger and stock_balances in ONE transaction. Deadlocks and lock wait timeouts re-run the
/// whole transaction (see <see cref="TransientRetry"/>).
/// </summary>
public sealed class StockPostingService(
    DbConnectionFactory connectionFactory,
    ILogger<StockPostingService>? logger = null)
{
    private readonly TransientRetry _retry =
        new((ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    /// <summary>
    /// Posts all movements of a document atomically. Later movements see the effect of earlier ones.
    /// "Today" for expiry checks is the branch-local date of each line's OccurredAt.
    /// </summary>
    public Task<IReadOnlyList<StockLedgerEntry>> PostAsync(
        IReadOnlyList<StockMovement> movements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(movements);

        return ExecuteAsync(
            movements.Select(m => (m.Line.LocationId, m.Line.ProductId)),
            context =>
            {
                var balances = context.Balances;
                var entries = new List<StockLedgerEntry>();

                foreach (var movement in movements)
                {
                    var line = movement.Line;
                    var buckets = StockBalances.BucketsFor(balances, line.LocationId, line.ProductId);
                    var trackExpiry = context.TrackExpiry[line.ProductId];

                    var allocations = movement.Direction switch
                    {
                        StockDirection.Out => StockAllocator.AllocateOutbound(buckets, movement.BaseQty, trackExpiry,
                            BranchClock.Today(line.OccurredAt, context.TimeZones[line.LocationId]), movement.AllowExpired),
                        StockDirection.In => StockAllocator.AllocateInbound(buckets, movement.BaseQty, movement.ExpiryDate, trackExpiry),
                        _ => throw new ArgumentOutOfRangeException(nameof(movements), movement.Direction, "Unknown stock direction."),
                    };

                    var lineEntries = StockLedgerBuilder.Build(line, movement.Direction, allocations);
                    balances = StockBalances.Apply(balances, lineEntries);
                    entries.AddRange(lineEntries);
                }

                return (IReadOnlyList<StockLedgerEntry>)entries;
            },
            entries => entries,
            cancellationToken);
    }

    /// <summary>LOCATION_TRANSFER inside this node; expiry is kept per FEFO allocation.</summary>
    public Task<StockTransferEntries> TransferAsync(
        StockTransferRequest request, bool allowExpired = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ExecuteAsync(
            [(request.FromLocationId, request.ProductId), (request.ToLocationId, request.ProductId)],
            context => StockTransfer.Transfer(request, context.Balances,
                BranchClock.Today(request.OccurredAt, context.TimeZones[request.FromLocationId]),
                context.TrackExpiry[request.ProductId], allowExpired),
            result => result.All,
            cancellationToken);
    }

    private sealed record PostingContext(
        IReadOnlyDictionary<StockBalanceKey, decimal> Balances,
        IReadOnlyDictionary<Guid, bool> TrackExpiry,
        IReadOnlyDictionary<Guid, string> TimeZones);

    private Task<T> ExecuteAsync<T>(
        IEnumerable<(Guid LocationId, Guid ProductId)> keys,
        Func<PostingContext, T> compute,
        Func<T, IReadOnlyList<StockLedgerEntry>> entriesOf,
        CancellationToken cancellationToken)
    {
        // Fixed lock order across all postings avoids deadlocks between multi-product documents.
        var lockOrder = keys.Distinct()
            .OrderBy(k => k.ProductId)
            .ThenBy(k => k.LocationId)
            .ToList();

        // Each attempt re-reads balances and re-allocates, so a retry never reuses stale state.
        return _retry.ExecuteAsync(ct => ExecuteOnceAsync(lockOrder, compute, entriesOf, ct), "Stock posting", cancellationToken);
    }

    private async Task<T> ExecuteOnceAsync<T>(
        IReadOnlyList<(Guid LocationId, Guid ProductId)> lockOrder,
        Func<PostingContext, T> compute,
        Func<T, IReadOnlyList<StockLedgerEntry>> entriesOf,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        // READ COMMITTED: locking reads see the latest committed rows and take no gap locks.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var trackExpiry = new Dictionary<Guid, bool>();
        var timeZones = new Dictionary<Guid, string>();
        foreach (var productId in lockOrder.Select(k => k.ProductId).Distinct())
        {
            trackExpiry[productId] = await StockRepository.GetTrackExpiryAsync(connection, transaction, productId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown product {productId}.");
        }
        foreach (var locationId in lockOrder.Select(k => k.LocationId).Distinct())
        {
            timeZones[locationId] = await StockRepository.GetLocationTimeZoneAsync(connection, transaction, locationId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown location {locationId}.");
        }

        var balances = new Dictionary<StockBalanceKey, decimal>();
        foreach (var (locationId, productId) in lockOrder)
        {
            var buckets = await StockRepository.LockBucketsAsync(connection, transaction, locationId, productId, cancellationToken);
            foreach (var bucket in buckets)
                balances[new StockBalanceKey(locationId, productId, bucket.ExpiryDate)] = bucket.BaseQty;
        }

        var result = compute(new PostingContext(balances, trackExpiry, timeZones));
        var entries = entriesOf(result);

        if (entries.Count > 0)
        {
            await StockRepository.InsertLedgerAsync(connection, transaction, entries, cancellationToken);
            await StockRepository.UpsertBalancesAsync(connection, transaction, entries, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
