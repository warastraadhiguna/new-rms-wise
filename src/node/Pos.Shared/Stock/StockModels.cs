namespace Pos.Shared.Stock;

/// <summary>Balance of one product at one location for one expiry date. <c>ExpiryDate</c> null = no/unknown expiry.</summary>
public readonly record struct StockBucket(DateOnly? ExpiryDate, decimal BaseQty);

/// <summary>Part of a movement assigned to one expiry bucket. <c>BaseQty</c> is always positive; direction is separate.</summary>
public readonly record struct StockAllocation(DateOnly? ExpiryDate, decimal BaseQty);

public enum StockDirection
{
    In,
    Out,
}

/// <summary>Key of <c>stock_balances</c> with the expiry as nullable date (see <see cref="StockExpiryKey"/>).</summary>
public readonly record struct StockBalanceKey(Guid LocationId, Guid ProductId, DateOnly? ExpiryDate);

/// <summary>One <c>stock_ledger</c> row. <c>BaseQty</c> is signed: + in, - out.</summary>
public sealed record StockLedgerEntry(
    Guid Id,
    Guid NodeId,
    Guid LocationId,
    Guid ProductId,
    DateOnly? ExpiryDate,
    decimal BaseQty,
    string DocType,
    Guid DocId,
    Guid? DocLineId,
    DateTime OccurredAt);

/// <summary>The document line a movement belongs to. <c>OccurredAt</c> is UTC from the Node Service clock.</summary>
public sealed record StockDocumentLine(
    Guid NodeId,
    Guid LocationId,
    Guid ProductId,
    string DocType,
    Guid DocId,
    Guid? DocLineId,
    DateTime OccurredAt);

/// <summary>Values of <c>stock_ledger.doc_type</c>.</summary>
public static class StockDocTypes
{
    public const string Sale = "SALE";

    /// <summary>A voided sale: every sold quantity back into its bucket (doc_id = sale_voids.id).</summary>
    public const string SaleVoid = "SALE_VOID";
    public const string SaleReturn = "SALE_RETURN";
    public const string GoodsReceipt = "GOODS_RECEIPT";
    public const string Delivery = "DELIVERY";
    public const string LocationTransfer = "LOCATION_TRANSFER";
    public const string Adjustment = "ADJUSTMENT";

    public static IReadOnlyList<string> All { get; } =
        [Sale, SaleVoid, SaleReturn, GoodsReceipt, Delivery, LocationTransfer, Adjustment];

    public static bool IsDefined(string? value) => value is not null && All.Contains(value);

    /// <summary>
    /// "Jenis Dokumen" on the Kartu Stok: Penjualan, Retur, Terima Barang, Mutasi (between nodes or locations),
    /// Penyesuaian, or Opname (an adjustment that comes from a stock count). Unknown types show their code.
    /// </summary>
    public static string Label(string docType, bool fromStockCount) => docType switch
    {
        Sale => "Penjualan",
        SaleVoid => "Void Penjualan",
        SaleReturn => "Retur",
        GoodsReceipt => "Terima Barang",
        Delivery or LocationTransfer => "Mutasi",
        Adjustment => fromStockCount ? "Opname" : "Penyesuaian",
        _ => docType,
    };
}

/// <summary>
/// <c>stock_balances.expiry_key</c> is NOT NULL (part of the primary key); '9999-12-31' stands for "no expiry".
/// </summary>
public static class StockExpiryKey
{
    public static readonly DateOnly NoExpiry = new(9999, 12, 31);

    public static DateOnly ToKey(DateOnly? expiryDate) => expiryDate ?? NoExpiry;

    public static DateOnly? FromKey(DateOnly key) => key == NoExpiry ? null : key;
}
