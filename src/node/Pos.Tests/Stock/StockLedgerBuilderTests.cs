using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

public class StockLedgerBuilderTests
{
    private static readonly Guid Node = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Location = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Product = Guid.Parse("00000000-0000-7000-8000-000000000003");
    private static readonly Guid Doc = Guid.Parse("00000000-0000-7000-8000-000000000004");
    private static readonly Guid DocLine = Guid.Parse("00000000-0000-7000-8000-000000000005");
    private static readonly DateTime At = Utc("2026-11-20T03:15:00.123Z");

    private static StockDocumentLine Line(string docType = StockDocTypes.Sale, DateTime? at = null) =>
        new(Node, Location, Product, docType, Doc, DocLine, at ?? At);

    private static Func<Guid> SequentialIds()
    {
        var n = 100;
        return () => Guid.Parse($"00000000-0000-7000-8000-{n++:D12}");
    }

    [Fact]
    public void Outbound_entries_are_negative_one_per_allocation()
    {
        StockAllocation[] allocations = [new(Day("2027-01-01"), 3m), new(Day("2027-03-01"), 2m), new(null, 1.5m)];

        var entries = StockLedgerBuilder.Build(Line(), StockDirection.Out, allocations, SequentialIds());

        Assert.Equal(3, entries.Count);
        Assert.Equal([-3m, -2m, -1.5m], entries.Select(e => e.BaseQty));
        Assert.Equal([Day("2027-01-01"), Day("2027-03-01"), (DateOnly?)null], entries.Select(e => e.ExpiryDate));
        Assert.All(entries, e =>
        {
            Assert.Equal(Node, e.NodeId);
            Assert.Equal(Location, e.LocationId);
            Assert.Equal(Product, e.ProductId);
            Assert.Equal(StockDocTypes.Sale, e.DocType);
            Assert.Equal(Doc, e.DocId);
            Assert.Equal(DocLine, e.DocLineId);
            Assert.Equal(At, e.OccurredAt);
            Assert.Equal(DateTimeKind.Utc, e.OccurredAt.Kind);
        });
    }

    [Fact]
    public void Inbound_entries_are_positive()
    {
        StockAllocation[] allocations = [new(null, 3m), new(Day("2027-06-30"), 2m)];

        var entries = StockLedgerBuilder.Build(Line(StockDocTypes.GoodsReceipt), StockDirection.In, allocations, SequentialIds());

        Assert.Equal([3m, 2m], entries.Select(e => e.BaseQty));
    }

    [Fact]
    public void Each_entry_gets_its_own_id()
    {
        StockAllocation[] allocations = [new(Day("2027-01-01"), 1m), new(null, 1m)];

        var entries = StockLedgerBuilder.Build(Line(), StockDirection.Out, allocations, SequentialIds());

        Assert.Equal(2, entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Default_ids_are_uuid_v7()
    {
        StockAllocation[] allocations = [new(Day("2027-01-01"), 1m), new(null, 1m)];

        var entries = StockLedgerBuilder.Build(Line(), StockDirection.Out, allocations);

        Assert.All(entries, e => Assert.Equal(7, e.Id.Version));
        Assert.Equal(2, entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Doc_line_id_is_optional()
    {
        var line = Line() with { DocLineId = null };

        var entries = StockLedgerBuilder.Build(line, StockDirection.Out, [new(null, 1m)], SequentialIds());

        Assert.Null(entries.Single().DocLineId);
    }

    [Fact]
    public void No_allocations_no_entries()
    {
        Assert.Empty(StockLedgerBuilder.Build(Line(), StockDirection.Out, [], SequentialIds()));
    }

    [Fact]
    public void Rejects_non_utc_time()
    {
        var local = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(
            () => StockLedgerBuilder.Build(Line(at: local), StockDirection.Out, [new(null, 1m)], SequentialIds()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SALES")]
    public void Rejects_unknown_doc_type(string docType)
    {
        Assert.Throws<ArgumentException>(
            () => StockLedgerBuilder.Build(Line(docType), StockDirection.Out, [new(null, 1m)], SequentialIds()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Rejects_non_positive_allocation(string qty)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StockLedgerBuilder.Build(Line(), StockDirection.Out, [new(null, D(qty))], SequentialIds()));
    }

    [Fact]
    public void Rejects_undefined_direction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StockLedgerBuilder.Build(Line(), (StockDirection)9, [new(null, 1m)], SequentialIds()));
    }

    [Fact]
    public void Doc_types_fit_the_column()
    {
        Assert.All(StockDocTypes.All, t => Assert.InRange(t.Length, 1, 30));
    }
}
