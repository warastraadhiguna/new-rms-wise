namespace Pos.Shared.Stock;

/// <summary>Turns allocations of one document line into <c>stock_ledger</c> entries (one per expiry bucket).</summary>
public static class StockLedgerBuilder
{
    /// <param name="newId">Id generator; defaults to UUID v7. Injectable for tests.</param>
    public static IReadOnlyList<StockLedgerEntry> Build(
        StockDocumentLine line,
        StockDirection direction,
        IEnumerable<StockAllocation> allocations,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(allocations);

        if (!StockDocTypes.IsDefined(line.DocType))
            throw new ArgumentException($"Unknown stock doc type '{line.DocType}'.", nameof(line));
        if (line.OccurredAt.Kind != DateTimeKind.Utc)
            throw new ArgumentException("OccurredAt must be UTC.", nameof(line));

        var sign = direction switch
        {
            StockDirection.In => 1m,
            StockDirection.Out => -1m,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown stock direction."),
        };
        newId ??= Guid.CreateVersion7;

        var entries = new List<StockLedgerEntry>();
        foreach (var allocation in allocations)
        {
            if (allocation.BaseQty <= 0)
                throw new ArgumentOutOfRangeException(nameof(allocations), allocation.BaseQty,
                    "Allocated quantity must be greater than zero.");

            entries.Add(new StockLedgerEntry(
                newId(),
                line.NodeId,
                line.LocationId,
                line.ProductId,
                allocation.ExpiryDate,
                sign * allocation.BaseQty,
                line.DocType,
                line.DocId,
                line.DocLineId,
                line.OccurredAt));
        }

        return entries;
    }
}
