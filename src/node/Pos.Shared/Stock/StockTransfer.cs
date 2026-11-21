namespace Pos.Shared.Stock;

/// <summary>Move of one product between two locations of the same node. Quantity in base units.</summary>
public sealed record StockTransferRequest(
    Guid NodeId,
    Guid ProductId,
    Guid FromLocationId,
    Guid ToLocationId,
    decimal BaseQty,
    Guid DocId,
    Guid? DocLineId,
    DateTime OccurredAt);

/// <summary>Ledger entries of a transfer: <c>Out</c> at the source (negative), <c>In</c> at the destination (positive).</summary>
public sealed record StockTransferEntries(IReadOnlyList<StockLedgerEntry> Out, IReadOnlyList<StockLedgerEntry> In)
{
    public IReadOnlyList<StockLedgerEntry> All => [.. Out, .. In];
}

/// <summary>One expiry of a location transfer: its line id, quantity, and the ledger entries out and in.</summary>
public sealed record StockTransferLine(
    Guid LineId, DateOnly? ExpiryDate, decimal BaseQty, IReadOnlyList<StockLedgerEntry> Out, IReadOnlyList<StockLedgerEntry> In);

/// <summary>One shipped line (expiry + qty) and how it lands in the destination buckets.</summary>
public sealed record ReceiptLineAllocation(StockAllocation Shipped, IReadOnlyList<StockAllocation> Allocations);

/// <summary>
/// Transfers keep the expiry of each FEFO allocation from the source to the destination.
/// Within a node use <see cref="Transfer"/>. Across nodes the sides are separate documents:
/// the sender allocates with <see cref="StockAllocator.AllocateOutbound"/> (one delivery line per expiry),
/// the receiver calls <see cref="AllocateReceipt"/> with the expiries read from the delivery lines.
/// </summary>
public static class StockTransfer
{
    /// <summary>
    /// Inbound side of a transfer. Each shipped line follows the inbound rule (a negative null bucket is
    /// covered first). Lines are processed earliest expiry first (null last, ties in input order) so the
    /// oldest goods cover the shortage; the result is returned in input order.
    /// </summary>
    public static IReadOnlyList<ReceiptLineAllocation> AllocateReceipt(
        IEnumerable<StockBucket> destinationBuckets,
        IReadOnlyList<StockAllocation> shippedLines,
        bool trackExpiry)
    {
        ArgumentNullException.ThrowIfNull(destinationBuckets);
        ArgumentNullException.ThrowIfNull(shippedLines);

        var nullBalance = destinationBuckets.Where(b => b.ExpiryDate is null).Sum(b => b.BaseQty);
        var results = new ReceiptLineAllocation[shippedLines.Count];

        var processingOrder = Enumerable.Range(0, shippedLines.Count)
            .OrderBy(i => StockExpiryKey.ToKey(shippedLines[i].ExpiryDate));

        foreach (var i in processingOrder)
        {
            var shipped = shippedLines[i];
            var allocations = StockAllocator.AllocateInbound(
                [new StockBucket(null, nullBalance)], shipped.BaseQty, shipped.ExpiryDate, trackExpiry);

            nullBalance += allocations.Where(a => a.ExpiryDate is null).Sum(a => a.BaseQty);
            results[i] = new ReceiptLineAllocation(shipped, allocations);
        }

        return results;
    }

    /// <summary>
    /// Same as <see cref="Transfer"/>, grouped per FEFO allocation of the source: one line per expiry (a
    /// <c>location_transfer_lines</c> row) whose out and in ledger entries carry that line's id as <c>DocLineId</c>.
    /// </summary>
    public static IReadOnlyList<StockTransferLine> TransferByExpiry(
        StockTransferRequest request,
        IReadOnlyDictionary<StockBalanceKey, decimal> balances,
        DateOnly today,
        bool trackExpiry,
        bool allowExpired = false,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(balances);
        if (request.FromLocationId == request.ToLocationId)
            throw new ArgumentException("Source and destination location must differ.", nameof(request));
        newId ??= Guid.CreateVersion7;

        var shipped = StockAllocator.AllocateOutbound(
            StockBalances.BucketsFor(balances, request.FromLocationId, request.ProductId),
            request.BaseQty, trackExpiry, today, allowExpired);
        var received = AllocateReceipt(
            StockBalances.BucketsFor(balances, request.ToLocationId, request.ProductId),
            shipped, trackExpiry);

        return received.Select(r =>
        {
            var lineId = newId();
            var lineRequest = request with { DocLineId = lineId };
            return new StockTransferLine(lineId, r.Shipped.ExpiryDate, r.Shipped.BaseQty,
                StockLedgerBuilder.Build(Line(lineRequest, request.FromLocationId), StockDirection.Out, [r.Shipped], newId),
                StockLedgerBuilder.Build(Line(lineRequest, request.ToLocationId), StockDirection.In, r.Allocations, newId));
        }).ToList();
    }

    /// <summary>
    /// Transfer between two locations of one node (<c>LOCATION_TRANSFER</c>): FEFO out of the source,
    /// then in at the destination with the same expiry per allocation. Total out == total in.
    /// </summary>
    public static StockTransferEntries Transfer(
        StockTransferRequest request,
        IReadOnlyDictionary<StockBalanceKey, decimal> balances,
        DateOnly today,
        bool trackExpiry,
        bool allowExpired = false,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(balances);
        if (request.FromLocationId == request.ToLocationId)
            throw new ArgumentException("Source and destination location must differ.", nameof(request));

        var shipped = StockAllocator.AllocateOutbound(
            StockBalances.BucketsFor(balances, request.FromLocationId, request.ProductId),
            request.BaseQty, trackExpiry, today, allowExpired);

        var received = AllocateReceipt(
            StockBalances.BucketsFor(balances, request.ToLocationId, request.ProductId),
            shipped, trackExpiry);

        var outEntries = StockLedgerBuilder.Build(Line(request, request.FromLocationId), StockDirection.Out, shipped, newId);
        var inEntries = StockLedgerBuilder.Build(
            Line(request, request.ToLocationId), StockDirection.In, received.SelectMany(r => r.Allocations), newId);

        return new StockTransferEntries(outEntries, inEntries);
    }

    private static StockDocumentLine Line(StockTransferRequest request, Guid locationId) =>
        new(request.NodeId, locationId, request.ProductId, StockDocTypes.LocationTransfer,
            request.DocId, request.DocLineId, request.OccurredAt);
}
