using System.Net;
using System.Text;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

/// <summary>A fake Node Service: records requests and answers with canned responses.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeServer Reply(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, status >= HttpStatusCode.BadRequest ? "application/problem+json" : "application/json"),
        });
        return this;
    }

    public FakeServer Problem(HttpStatusCode status, string code, string extra = "") =>
        Reply(status, $$"""{"title":"{{code}}","status":{{(int)status}},"detail":"server detail","code":"{{code}}"{{extra}}}""");

    public FakeServer Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        if (_responses.Count == 0)
            throw new InvalidOperationException($"No response prepared for {request.Method} {request.RequestUri}");
        return _responses.Dequeue()(request);
    }
}

/// <summary>Scriptable INodeApi for ViewModel tests.</summary>
public sealed class FakeNodeApi : INodeApi
{
    public Func<string, string, LoginResult>? OnLogin { get; set; }
    public Func<ShiftInfo?>? OnCurrentShift { get; set; }
    public Func<decimal, ShiftInfo>? OnOpenShift { get; set; }

    public List<string> Calls { get; } = [];

    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        Calls.Add($"login:{username}");
        return Task.FromResult(OnLogin!(username, password));
    }

    public Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default)
    {
        Calls.Add("current-shift");
        return Task.FromResult(OnCurrentShift!());
    }

    public Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default)
    {
        Calls.Add($"open-shift:{openingCash}");
        return Task.FromResult(OnOpenShift!(openingCash));
    }

    /// <summary>Products returned by search: by default every product whose code, barcode or name contains the query.</summary>
    public List<ProductItem> Catalog { get; } = [];
    public Func<Guid, IReadOnlyList<ProductItem>>? OnUnits { get; set; }
    public List<CustomerItem> Customers { get; } = [];
    public Func<string, string, string, ApprovalResult>? OnApprove { get; set; }
    public Func<Guid, string, decimal, IReadOnlyList<CartCancellationLine>, CartCancellationResult>? OnCancelCart { get; set; }

    public Task<IReadOnlyList<ProductItem>> SearchProductsAsync(string query, CancellationToken ct = default)
    {
        Calls.Add($"search:{query}");
        IReadOnlyList<ProductItem> result = Catalog
            .Select(p => p.Barcode == query ? p with { MatchedBy = "barcode" }
                : p.Sku == query ? p with { MatchedBy = "sku" }
                : p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? p with { MatchedBy = "name" }
                : null)
            .Where(p => p is not null)
            .OrderBy(p => p!.MatchedBy == "name" ? 1 : 0)
            .ToList()!;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProductItem>> GetProductUnitsAsync(Guid productId, CancellationToken ct = default)
    {
        Calls.Add($"units:{productId}");
        return Task.FromResult(OnUnits!(productId));
    }

    public Task<IReadOnlyList<CustomerItem>> SearchCustomersAsync(string query, CancellationToken ct = default)
    {
        Calls.Add($"customers:{query}");
        IReadOnlyList<CustomerItem> result = Customers
            .Where(c => c.Code == query || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(result);
    }

    public Task<ApprovalResult> ApproveAsync(string action, string username, string password, CancellationToken ct = default)
    {
        Calls.Add($"approve:{action}:{username}");
        return Task.FromResult(OnApprove!(action, username, password));
    }

    public Task<CartCancellationResult> CancelCartAsync(
        Guid id, string reason, decimal totalAmount, IReadOnlyList<CartCancellationLine> lines, CancellationToken ct = default)
    {
        Calls.Add($"cancel-cart:{reason}");
        return Task.FromResult(OnCancelCart!(id, reason, totalAmount, lines));
    }

    public SalesConfig Config { get; set; } = Samples.Config();
    public Func<CreateSaleRequest, SaleResult>? OnCreateSale { get; set; }
    public List<CreateSaleRequest> SaleRequests { get; } = [];

    public Task<SalesConfig> GetSalesConfigAsync(CancellationToken ct = default)
    {
        Calls.Add("sales-config");
        return Task.FromResult(Config);
    }

    /// <summary>Default: synced just now, nothing pending. Throw a NodeApiException to simulate an unreachable Node Service.</summary>
    public Func<SyncStatusInfo>? OnSyncStatus { get; set; }

    public Task<SyncStatusInfo> GetSyncStatusAsync(CancellationToken ct = default)
    {
        Calls.Add("sync-status");
        return Task.FromResult((OnSyncStatus ?? (() => new SyncStatusInfo(true, "ok", DateTime.UtcNow, 0, 0, "Sinkron: baru saja · 0 tertunda", false)))());
    }

    public Task<SaleResult> CreateSaleAsync(CreateSaleRequest request, CancellationToken ct = default)
    {
        Calls.Add("create-sale");
        SaleRequests.Add(request);
        return Task.FromResult(OnCreateSale!(request));
    }

    /// <summary>GET /sales/{id}; default: a cash sale with that id. Throw from it to simulate a failure.</summary>
    public Func<Guid, SaleDetail>? OnGetSale { get; set; }

    public Task<SaleDetail> GetSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        Calls.Add($"get-sale:{saleId}");
        return Task.FromResult((OnGetSale ?? (id => Receipts.Detail(id)))(saleId));
    }

    // ---- stock screens ---------------------------------------------------------------------------

    public List<StockLocation> StockLocations { get; } = [Stock.Display, Stock.Storage];

    /// <summary>Rows served by /stock/totals: filtered by SKU/name and location, then paged like the server.</summary>
    public List<StockTotalRow> StockRows { get; } = [];

    public Func<Guid, Guid?, StockExpiry>? OnStockExpiry { get; set; }

    public Task<IReadOnlyList<StockLocation>> GetStockLocationsAsync(CancellationToken ct = default)
    {
        Calls.Add("stock-locations");
        return Task.FromResult<IReadOnlyList<StockLocation>>(StockLocations);
    }

    /// <summary>Extra zero-stock rows, served only with include_empty.</summary>
    public List<StockTotalRow> EmptyStockRows { get; } = [];

    public Task<StockTotalsPage> GetStockTotalsAsync(
        string? query, Guid? locationId, int offset, int limit, CancellationToken ct = default, bool includeEmpty = false)
    {
        Calls.Add($"stock-totals:{query}:{locationId?.ToString() ?? "all"}:{offset}:{limit}" + (includeEmpty ? ":empty" : ""));
        var q = (query ?? "").Trim();
        var matching = StockRows.Concat(includeEmpty ? EmptyStockRows : [])
            .Where(r => locationId is null || r.LocationId == locationId)
            .Where(r => q.Length == 0 || r.Sku == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(new StockTotalsPage(matching.Count, offset, limit, matching.Skip(offset).Take(limit).ToList()));
    }

    public Task<StockExpiry> GetStockExpiryAsync(Guid productId, Guid? locationId, CancellationToken ct = default)
    {
        Calls.Add($"stock-expiry:{productId}:{locationId?.ToString() ?? "all"}");
        return Task.FromResult((OnStockExpiry ?? ((id, location) => Stock.Expiry(id, location)))(productId, locationId));
    }

    /// <summary>The whole card (all rows); each call returns the requested page of it, like the server.</summary>
    public StockCard StockCardData { get; set; } = Stock.Card(3);

    public Task<StockCard> GetStockCardAsync(Guid productId, Guid? locationId, DateOnly from, DateOnly to, int offset, int limit, CancellationToken ct = default)
    {
        Calls.Add($"stock-card:{productId}:{locationId?.ToString() ?? "all"}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{offset}:{limit}");
        var card = StockCardData;
        return Task.FromResult(card with
        {
            ProductId = productId,
            LocationId = locationId,
            From = from,
            To = to,
            Offset = offset,
            Limit = limit,
            Rows = card.Rows.Skip(offset).Take(limit).ToList(),
        });
    }



    // ---- data penjualan ---------------------------------------------------------------------------

    /// <summary>Rows of Data Penjualan (filtered here by q on number, member and cashier, like the server).</summary>
    public List<SalesListRow> SalesRows { get; } = [];

    /// <summary>The first date a cashier may see (null = sale.history).</summary>
    public DateOnly? SalesEarliestDate { get; set; }

    public List<(DateOnly From, DateOnly To, string? Q, int Offset, int Limit)> SalesQueries { get; } = [];

    public Task<SalesListPage> GetSalesAsync(DateOnly from, DateOnly to, string? q, int offset, int limit, CancellationToken ct = default)
    {
        Calls.Add($"sales:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{q}:{offset}:{limit}");
        SalesQueries.Add((from, to, q, offset, limit));
        var text = q ?? "";
        var matching = SalesRows.Where(r => text.Length == 0
            || r.Number.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (r.CustomerName ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.CashierName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(new SalesListPage(matching.Skip(offset).Take(limit).ToList(), matching.Count, "Asia/Jakarta", SalesEarliestDate));
    }

    // ---- void / reasons ---------------------------------------------------------------------------

    public List<ReasonItem> Reasons { get; } =
    [
        new(Guid.Parse("01930000-0000-7000-8000-0000000000e1"), "SALE_VOID", "Salah input"),
        new(Guid.Parse("01930000-0000-7000-8000-0000000000e2"), "SALE_VOID", "Pembeli batal"),
        new(Guid.Parse("01930000-0000-7000-8000-0000000000f1"), "RETURN", "Barang rusak"),
        new(Guid.Parse("01930000-0000-7000-8000-0000000000f2"), "RETURN", "Salah barang"),
    ];

    public Task<IReadOnlyList<ReasonItem>> GetReasonsAsync(string type, CancellationToken ct = default)
    {
        Calls.Add($"reasons:{type}");
        return Task.FromResult<IReadOnlyList<ReasonItem>>(Reasons.Where(r => r.Type == type).ToList());
    }

    public List<(Guid SaleId, VoidSaleRequest Request)> VoidRequests { get; } = [];

    /// <summary>Thrown by the next VoidSaleAsync (then cleared).</summary>
    public Exception? NextVoidFailure { get; set; }

    public Task<SaleVoidInfo> VoidSaleAsync(Guid saleId, VoidSaleRequest request, CancellationToken ct = default)
    {
        Calls.Add($"void:{saleId}");
        VoidRequests.Add((saleId, request));
        if (NextVoidFailure is { } failure)
        {
            NextVoidFailure = null;
            throw failure;
        }
        var index = SalesRows.FindIndex(r => r.Id == saleId);
        var number = index >= 0 ? SalesRows[index].Number : "TK01-SL-202611-000001";
        if (index >= 0)
            SalesRows[index] = SalesRows[index] with { Status = "VOID", Outstanding = 0m };
        return Task.FromResult(new SaleVoidInfo(request.Id, saleId, number, request.ReasonId, "Salah input", request.Notes,
            new DateTime(2026, 11, 22, 4, 0, 0, DateTimeKind.Utc), Samples.UserId, "Kasir Satu", null, null));
    }

    // ---- retur penjualan --------------------------------------------------------------------------

    /// <summary>Notas that can be returned, by sale id (also found by number).</summary>
    public Dictionary<Guid, ReturnSource> ReturnSources { get; } = [];
    public List<CreateSaleReturnRequest> ReturnRequests { get; } = [];
    public Dictionary<Guid, SaleReturnInfo> StoredReturns { get; } = [];

    /// <summary>Thrown by the next CreateReturnAsync (then cleared).</summary>
    public Exception? NextReturnFailure { get; set; }

    public Task<ReturnSource> GetReturnSourceAsync(Guid? saleId, string? number, CancellationToken ct = default)
    {
        Calls.Add(saleId is { } id ? $"return-source:{id}" : $"return-source:{number}");
        var source = saleId is { } key && ReturnSources.TryGetValue(key, out var byId) ? byId
            : ReturnSources.Values.FirstOrDefault(s => s.SaleNumber == number);
        return source is not null
            ? Task.FromResult(source)
            : Task.FromException<ReturnSource>(new NodeApiException("Nota tidak ditemukan di Node Service.", "unknown_sale", 404));
    }

    public Task<SaleReturnInfo> CreateReturnAsync(CreateSaleReturnRequest request, CancellationToken ct = default)
    {
        Calls.Add($"create-return:{request.Id}");
        ReturnRequests.Add(request);
        if (NextReturnFailure is { } failure)
        {
            NextReturnFailure = null;
            return Task.FromException<SaleReturnInfo>(failure);
        }
        if (StoredReturns.TryGetValue(request.Id, out var stored))
            return Task.FromResult(stored);

        var source = ReturnSources[request.SaleId];
        var lines = request.Lines.Select((l, i) =>
        {
            var line = source.Lines.Single(s => s.SaleLineId == l.SaleLineId);
            var amount = Pos.Shared.Sales.ReturnPricing.RefundAmount(line.NetLineTotal, line.SoldQty, line.ReturnedQty, l.Qty, 0);
            return new SaleReturnLine(i + 1, line.SaleLineId, line.ProductId, line.Sku, line.Name, Guid.CreateVersion7(), line.UnitCode,
                l.Qty, l.Qty, line.NetUnitPrice, amount, l.Condition, Guid.CreateVersion7(), l.Condition == "DAMAGED" ? "RUSAK" : "DISPLAY", []);
        }).ToList();
        var number = $"TK01-RT-202611-{StoredReturns.Count + 1:000000}";
        var ret = new SaleReturnInfo(request.Id, number, source.SaleId, source.SaleNumber, null,
            new DateTime(2026, 11, 22, 9, 45, 0, DateTimeKind.Utc), "Asia/Jakarta", request.Settlement, lines.Sum(l => l.LineTotal),
            request.Settlement == "REFUND" ? lines.Sum(l => l.LineTotal) : 0m, request.ReasonId, "Barang rusak", request.Notes,
            Samples.UserId, "Kasir Satu", null, null, source.CustomerCode, source.CustomerName, null, null, lines,
            new ReceiptText("TOKO DEV", null, null, null, null, null));
        StoredReturns[request.Id] = ret;
        ReturnSources[source.SaleId] = source with
        {
            Lines = source.Lines.Select(s => request.Lines.FirstOrDefault(l => l.SaleLineId == s.SaleLineId) is { } r
                ? s with { ReturnedQty = s.ReturnedQty + r.Qty, ReturnableQty = s.ReturnableQty - r.Qty }
                : s).ToList(),
        };
        return Task.FromResult(ret);
    }

    public Task<IReadOnlyList<SaleReturnSummary>> GetReturnsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Calls.Add($"returns:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}");
        return Task.FromResult<IReadOnlyList<SaleReturnSummary>>(StoredReturns.Values
            .Select(r => new SaleReturnSummary(r.Id, r.Number, r.ReturnedAt, r.SaleNumber, r.CustomerName, r.CreatedByName, r.Settlement,
                r.Total, r.Notes, r.ExchangeSaleId is not null, r.TimeZone))
            .OrderByDescending(r => r.Number).ToList());
    }

    public Task<SaleReturnInfo> GetReturnAsync(Guid returnId, CancellationToken ct = default)
    {
        Calls.Add($"return:{returnId}");
        return Task.FromResult(StoredReturns[returnId]);
    }
    // ---- tutup shift ------------------------------------------------------------------------------

    /// <summary>Returned by GetShiftClosingAsync (default: <see cref="Shifts.Blind"/>).</summary>
    public Func<Guid, ShiftSummary>? OnShiftClosing { get; set; }

    /// <summary>Answers CloseShiftAsync (default: <see cref="Shifts.Closed"/> with the counted cash and notes).</summary>
    public Func<CloseShiftRequest, ShiftSummary>? OnCloseShift { get; set; }

    public List<CloseShiftRequest> CloseRequests { get; } = [];

    /// <summary>Thrown by the next CloseShiftAsync (then cleared), e.g. a network failure.</summary>
    public Exception? NextCloseFailure { get; set; }

    public Task<ShiftSummary> GetShiftClosingAsync(Guid shiftId, CancellationToken ct = default)
    {
        Calls.Add($"shift-closing:{shiftId}");
        return Task.FromResult(OnShiftClosing?.Invoke(shiftId) ?? Shifts.Blind(shiftId));
    }

    public Task<ShiftSummary> CloseShiftAsync(CloseShiftRequest request, CancellationToken ct = default)
    {
        Calls.Add($"close-shift:{request.ShiftId}:{request.CountedCash}");
        CloseRequests.Add(request);
        if (NextCloseFailure is { } failure)
        {
            NextCloseFailure = null;
            throw failure;
        }
        return Task.FromResult(OnCloseShift?.Invoke(request) ?? Shifts.Closed(request.ShiftId, request.CountedCash, request.Notes));
    }
    // ---- location transfers -----------------------------------------------------------------------

    /// <summary>Base stock at the source per product (default 30) for the preview.</summary>
    public Dictionary<Guid, decimal> SourceStock { get; } = [];
    public List<(Guid From, IReadOnlyList<LocationTransferLineRequest> Lines)> Previews { get; } = [];
    public List<Guid?> PreviewTargets { get; } = [];
    public List<CreateLocationTransferRequest> TransferRequests { get; } = [];

    /// <summary>Thrown by the next CreateLocationTransferAsync (then cleared), e.g. a network failure.</summary>
    public Exception? NextTransferFailure { get; set; }
    public List<LocationTransferSummary> TransferList { get; } = [];

    public Task<LocationTransferPreview> PreviewLocationTransferAsync(
        Guid fromLocationId, IReadOnlyList<LocationTransferLineRequest> lines, CancellationToken ct = default, Guid? toLocationId = null)
    {
        Calls.Add($"transfer-preview:{fromLocationId}:{lines.Count}");
        Previews.Add((fromLocationId, lines));
        PreviewTargets.Add(toLocationId);
        var used = new Dictionary<Guid, decimal>();
        var result = lines.Select((l, i) =>
        {
            var product = Catalog.FirstOrDefault(p => p.ProductUnitId == l.ProductUnitId) ?? Samples.Amplop;
            var baseQty = l.Qty * product.Conversion;
            var available = Math.Max(0, SourceStock.GetValueOrDefault(product.ProductId, 30m) - used.GetValueOrDefault(product.ProductId));
            used[product.ProductId] = used.GetValueOrDefault(product.ProductId) + baseQty;
            var dated = Math.Min(baseQty, available);
            List<ExpiryQty> allocations = [new(new DateOnly(2026, 12, 2), dated)];
            if (baseQty > dated)
                allocations.Add(new ExpiryQty(null, baseQty - dated));
            return new LocationTransferPreviewLine(i + 1, product.ProductId, l.ProductUnitId, l.Qty, baseQty, "PCS",
                SourceStock.GetValueOrDefault(product.ProductId, 30m), available, Math.Max(0, baseQty - available), allocations);
        }).ToList();
        return Task.FromResult(new LocationTransferPreview(new DateOnly(2026, 11, 22), result));
    }

    public Task<LocationTransfer> CreateLocationTransferAsync(CreateLocationTransferRequest request, CancellationToken ct = default)
    {
        Calls.Add($"transfer-create:{request.Id}");
        TransferRequests.Add(request);
        if (NextTransferFailure is { } failure)
        {
            NextTransferFailure = null;
            throw failure;
        }
        return Task.FromResult(Transfers.Sample(request.Id) with
        {
            FromLocationId = request.FromLocationId,
            ToLocationId = request.ToLocationId,
            Notes = request.Notes,
            ReversesId = request.ReversesId,
        });
    }

    public Task<IReadOnlyList<LocationTransferSummary>> GetLocationTransfersAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Calls.Add($"transfers:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}");
        return Task.FromResult<IReadOnlyList<LocationTransferSummary>>(TransferList);
    }

    /// <summary>The detail returned by GetLocationTransferAsync (default: <see cref="Transfers.Sample"/>).</summary>
    public Func<Guid, LocationTransfer>? OnTransfer { get; set; }

    public Task<LocationTransfer> GetLocationTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        Calls.Add($"transfer:{transferId}");
        return Task.FromResult(OnTransfer?.Invoke(transferId) ?? Transfers.Sample(transferId));
    }
}

public static class Transfers
{
    /// <summary>TK01-MT-202611-000001: STORAGE → DISPLAY, 2 PAK amplop (20 PCS over two expiry dates).</summary>
    public static LocationTransfer Sample(Guid id) => new(
        id, Guid.CreateVersion7(), "TK01-MT-202611-000001", Stock.Storage.Id, "STORAGE", Stock.Display.Id, "DISPLAY",
        new DateTime(2026, 11, 22, 3, 30, 0, DateTimeKind.Utc), "Asia/Jakarta", "isi etalase", Samples.UserId, "Admin Toko Dev",
        [
            new LocationTransferLine(1, Samples.AmplopId, "13589", "2023_amplop lebaran karakter", Samples.AmplopPak.ProductUnitId, "PAK", 2m,
                "PCS", 20m, [new ExpiryQty(new DateOnly(2026, 12, 2), 12m), new ExpiryQty(null, 8m)]),
        ]);
}

/// <summary>Scripted answers for the sales screen's dialogs; records what was asked.</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<string> Asked { get; } = [];
    public decimal? Quantity { get; set; }
    public SupervisorCredentials? Supervisor { get; set; }
    public string? CancelReason { get; set; }
    public bool ConfirmAnswer { get; set; }
    public ProductItem? ProductToPick { get; set; }
    public Func<IReadOnlyList<ProductItem>, ProductItem?>? UnitToPick { get; set; }
    public CustomerItem? CustomerToPick { get; set; }

    public decimal? AskQuantity(string productName, decimal current) { Asked.Add($"qty:{productName}:{current}"); return Quantity; }
    public SupervisorCredentials? AskSupervisor(string purpose) { Asked.Add($"supervisor:{purpose}"); return Supervisor; }
    public string? AskCancelReason() { Asked.Add("cancel-reason"); return CancelReason; }
    public bool Confirm(string message) { Asked.Add($"confirm:{message}"); return ConfirmAnswer; }
    public ProductItem? PickProduct(string initialQuery) { Asked.Add($"pick-product:{initialQuery}"); return ProductToPick; }

    public ProductItem? PickUnit(string productName, IReadOnlyList<ProductItem> units, Guid currentUnitId)
    {
        Asked.Add($"pick-unit:{productName}");
        return UnitToPick?.Invoke(units);
    }

    public CustomerItem? PickCustomer(string initialQuery) { Asked.Add($"pick-customer:{initialQuery}"); return CustomerToPick; }

    /// <summary>Plays the cashier in the payment dialog: gets the dialog's ViewModel, returns what the dialog would.</summary>
    public Func<ViewModels.PaymentViewModel, SaleResult?>? OnPayment { get; set; }
    public List<ViewModels.PaymentViewModel> Payments { get; } = [];
    public List<SaleResult> ShownResults { get; } = [];

    public SaleResult? ShowPayment(ViewModels.PaymentViewModel payment)
    {
        Asked.Add("payment");
        Payments.Add(payment);
        return OnPayment?.Invoke(payment);
    }

    public void ShowSaleResult(SaleResult result) { Asked.Add("sale-result"); ShownResults.Add(result); }

    public List<(string Message, IReadOnlyList<string> Lines)> Previews { get; } = [];

    public void ShowReceiptPreview(string message, IReadOnlyList<string> lines) { Asked.Add("receipt-preview"); Previews.Add((message, lines)); }

    /// <summary>Path returned by "Export to File"; null = the cashier cancelled the save dialog.</summary>
    public string? SaveFilePath { get; set; }

    public string? AskSaveFile(string suggestedFileName) { Asked.Add($"save-file:{suggestedFileName}"); return SaveFilePath; }

    public List<(string Title, string Message, IReadOnlyList<string> Lines)> Documents { get; } = [];




    /// <summary>Answer of AskText (e.g. the nota number for Tambah Retur); null = cancelled.</summary>
    public string? TextToAnswer { get; set; }

    public string? AskText(string title, string prompt) { Asked.Add($"text:{title}"); return TextToAnswer; }
    /// <summary>Answer of the reason dialog (null = cancelled); records the reasons offered.</summary>
    public ReasonChoice? ReasonToChoose { get; set; }
    public List<(string Title, IReadOnlyList<ReasonItem> Reasons)> ReasonDialogs { get; } = [];

    public ReasonChoice? AskReason(string title, string prompt, IReadOnlyList<ReasonItem> reasons)
    {
        Asked.Add($"reason:{title}");
        ReasonDialogs.Add((title, reasons));
        return ReasonToChoose;
    }
    public List<string> Messages { get; } = [];

    public void ShowMessage(string message) { Asked.Add($"message:{message}"); Messages.Add(message); }

    /// <summary>Plays the user in the Tutup Shift dialog; returns whether the shift got closed. Default: not closed.</summary>
    public Func<ViewModels.CloseShiftViewModel, bool>? OnCloseShift { get; set; }
    public List<ViewModels.CloseShiftViewModel> CloseShiftDialogs { get; } = [];

    public bool ShowCloseShift(ViewModels.CloseShiftViewModel closeShift)
    {
        Asked.Add("close-shift");
        CloseShiftDialogs.Add(closeShift);
        return OnCloseShift?.Invoke(closeShift) ?? false;
    }
    /// <summary>The action button text offered per detail (null = none).</summary>
    public List<string?> DocumentActions { get; } = [];

    /// <summary>True = the user presses the offered action button (e.g. "Buat Mutasi Balik").</summary>
    public bool ChooseDocumentAction { get; set; }

    public bool ShowDocumentDetail(string title, string message, IReadOnlyList<string> lines, string? actionText = null)
    {
        Asked.Add($"document:{title}");
        Documents.Add((title, message, lines));
        DocumentActions.Add(actionText);
        return actionText is not null && ChooseDocumentAction;
    }
}

public sealed class MemoryCartStore : ICartStore
{
    public CartSnapshot? Saved { get; set; }
    public int Saves { get; private set; }

    public CartSnapshot? Load() => Saved;
    public void Save(CartSnapshot snapshot) { Saved = snapshot; Saves++; }
    public void Clear() => Saved = null;
}

public sealed class MemoryViewSettingsStore : IViewSettingsStore
{
    public SalesViewSettings Settings { get; set; } = new();
    public SalesViewSettings Load() => Settings;
    public void Save(SalesViewSettings settings) => Settings = settings;
}

public sealed class FakeNavigator : INavigator
{
    public List<string> Shown { get; } = [];

    public void ShowLogin() => Shown.Add("login");
    public void ShowOpenShift() => Shown.Add("open-shift");
    public void ShowMain() => Shown.Add("main");
}

public static class Samples
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");
    public static readonly Guid OtherUserId = Guid.Parse("01930000-0000-7000-8000-000000000002");
    public static readonly Guid DeviceId = Guid.Parse("01930000-0000-7000-8000-0000000000d1");

    public static LoginResult Login(Guid? userId = null) => new(
        "jwt-token", new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc),
        new UserInfo(userId ?? UserId, "kasir1", "Kasir Satu"),
        ["sale.create", "shift.open"],
        new NodeSummary(Guid.CreateVersion7(), "TK01", "Toko Dev"));

    public static ShiftInfo Shift(Guid? cashierId = null) => new(
        Guid.CreateVersion7(), "TK01-SH-202611-000007", cashierId ?? UserId, "OPEN",
        new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc), 200000m);

    public static readonly Guid AmplopId = Guid.Parse("01930000-0000-7000-8000-00000000a001");
    public static readonly Guid KantongId = Guid.Parse("01930000-0000-7000-8000-00000000b001");
    public static readonly Guid VoucherId = Guid.Parse("01930000-0000-7000-8000-00000000c001");

    public static ProductItem Product(Guid productId, string sku, string name, decimal price, string unit = "PCS",
        decimal conversion = 1m, bool allowMultiplier = true, decimal stock = 100m, string? barcode = null, Guid? unitId = null,
        string? baseUnit = null) =>
        new(productId, unitId ?? Guid.CreateVersion7(), sku, name, barcode ?? "899" + sku, unit, unit, conversion,
            AllowDecimal: false, AllowQtyMultiplier: allowMultiplier, TrackExpiry: false, "Merek X", "Jenis Y", "Rak A",
            price, null, [new PriceTierInfo(1m, price)], stock, stock / conversion, "name",
            baseUnit ?? (conversion == 1m ? unit : "PCS"), BaseAllowDecimal: false);

    public static readonly ProductItem Amplop = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 7500m);
    public static readonly ProductItem AmplopPak = Product(AmplopId, "13589", "2023_amplop lebaran karakter", 70000m, unit: "PAK", conversion: 10m);
    public static readonly ProductItem Kantong = Product(KantongId, "5113", "2_HD kantong hitam 3kg", 4500m, unit: "BUNGKUS");
    public static readonly ProductItem Voucher = Product(VoucherId, "VCR10", "Voucher pulsa 10rb", 10500m, allowMultiplier: false);

    public static LoginResult LoginWith(params string[] permissions) => Login() with { Permissions = permissions };

    public static SalesConfig Config(decimal? discountMax = 10m, decimal step = 100m) => new(
        [
            new PaymentMethodInfo(Guid.CreateVersion7(), "CASH", "TUNAI", IsCash: true, IsCredit: false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "DEBIT", "DEBIT", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "QRIS", "QRIS", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "TRANSFER", "TRANSFER", false, false),
            new PaymentMethodInfo(Guid.CreateVersion7(), "CREDIT", "KREDIT", false, IsCredit: true),
        ],
        step, "nearest", 0, discountMax);

    /// <summary>What the server would answer for a request (totals are not recomputed here).</summary>
    public static SaleResult Saved(CreateSaleRequest request, string number, decimal total, decimal change, params SaleWarningInfo[] warnings) =>
        new(new SaleSummary(request.Id, number, total, 0m, 0m, total, change, 0m), warnings, Replayed: false);
}

/// <summary>Records RAW print jobs; <see cref="Failure"/> makes the next jobs fail like an offline printer.</summary>
public sealed class FakePrinter : IReceiptPrinter
{
    public List<(string Printer, byte[] Data, string Document)> Jobs { get; } = [];
    public string? Failure { get; set; }

    public void PrintRaw(string printerName, byte[] data, string documentName)
    {
        if (Failure is not null)
            throw new PrinterException(Failure);
        Jobs.Add((printerName, data, documentName));
    }
}

public sealed class MemoryLastSaleStore : ILastSaleStore
{
    public Guid? SaleId { get; set; }
    public Guid? Load() => SaleId;
    public void Save(Guid saleId) => SaleId = saleId;
}


/// <summary>Shift summaries as the Node Service returns them (opened 07:00 WIB, closed 15:05 WIB).</summary>
public static class Shifts
{
    public static readonly IReadOnlyList<Pos.Shared.Sales.ShiftMethodTotal> Methods =
    [
        new("DEBIT", "DEBIT", 20000m, 1), new("QRIS", "QRIS", 40000m, 1),
        new("TRANSFER", "TRANSFER", 50000m, 1), new("CREDIT", "KREDIT", 55000m, 2),
    ];

    /// <summary>Blind count, open: header only, no figures.</summary>
    public static ShiftSummary Blind(Guid id, Guid? cashierId = null) => new(
        id, "TK01-SH-202611-000007", cashierId ?? Samples.UserId, "Kasir Satu", "OPEN",
        new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), null, null, null, "Asia/Jakarta", true, 200000m,
        null, null, null, null, null, null, null, null, null, null, null,
        new ReceiptText("TOKO DEV", "Jl. Merdeka No. 1", null, null, null, null));

    /// <summary>Blind count off, open: the running figures (expected 268.000).</summary>
    public static ShiftSummary Open(Guid id, Guid? cashierId = null) => Blind(id, cashierId) with
    {
        BlindCount = false,
        CashSales = 40000m, CreditDownPayments = 15000m, ReceivableCash = 25000m, CashExpenses = 12000m,
        NonCash = Methods, SaleCount = 7, CancellationCount = 2, ExpectedCash = 268000m,
    };

    /// <summary>Closed with the counted cash: expected 268.000.</summary>
    public static ShiftSummary Closed(Guid id, decimal counted, string? notes = null, Guid? cashierId = null, Guid? closedBy = null) =>
        Open(id, cashierId) with
        {
            BlindCount = true,
            Status = "CLOSED",
            ClosedAt = new DateTime(2026, 11, 22, 8, 5, 0, DateTimeKind.Utc),
            ClosedById = closedBy ?? cashierId ?? Samples.UserId,
            ClosedByName = closedBy is null ? "Kasir Satu" : "Admin Toko Dev",
            CountedCash = counted,
            Difference = counted - 268000m,
            Notes = notes,
        };
}

/// <summary>Data Penjualan rows (sold 02:15 UTC = 09:15 WIB on 22/11/2026).</summary>
public static class SalesRows
{
    public static SalesListRow Cash(string number = "TK01-SL-202611-000101", Guid? id = null) => new(
        id ?? Guid.CreateVersion7(), number, new DateTime(2026, 11, 22, 2, 15, 0, DateTimeKind.Utc), null, null, "Kasir Satu", "TUNAI",
        20000m, null, 0m, 20000m, 0m, 0m, "PAID", null, null, true);

    public static SalesListRow Credit(string number = "TK01-SL-202611-000102", Guid? id = null) => new(
        id ?? Guid.CreateVersion7(), number, new DateTime(2026, 11, 22, 3, 40, 0, DateTimeKind.Utc), "C000001", "Budi Pelanggan", "Kasir Satu",
        "TUNAI + KREDIT", 62500m, 4m, 2500m, 60000m, 45000m, 45000m, "UNPAID", new DateOnly(2026, 12, 6), "ambil besok", true);

    public static SalesListRow Voided(string number = "TK01-SL-202611-000103") => Cash(number) with { Status = "VOID" };
}

/// <summary>Notas for the return form (sold 22/11/2026 02:15 UTC = 09:15 WIB).</summary>
public static class Returns
{
    public static readonly Guid SaleId = Guid.Parse("01930000-0000-7000-8000-00000000d001");
    public static readonly Guid AmplopLine = Guid.Parse("01930000-0000-7000-8000-00000000d101");
    public static readonly Guid KantongLine = Guid.Parse("01930000-0000-7000-8000-00000000d102");

    /// <summary>3 PCS amplop at net 6.750 (20.250) and 2 BUNGKUS kantong at 4.500 (9.000); cash nota within the window.</summary>
    public static ReturnSource Source(bool credit = false, bool withinWindow = true, decimal amplopReturned = 0m) => new(
        SaleId, "TK01-SL-202611-000123", new DateTime(2026, 11, 22, 2, 15, 0, DateTimeKind.Utc), "Asia/Jakarta", new DateOnly(2026, 11, 22),
        credit ? "C000001" : null, credit ? "Budi Pelanggan" : null, "Kasir Satu", 29250m, credit ? 20000m : 0m, credit ? 20000m : 0m,
        false, 7, withinWindow, 0,
        [
            new ReturnSourceLine(AmplopLine, 1, Samples.AmplopId, "13589", "2023_amplop lebaran karakter", "PCS", 3m, amplopReturned,
                3m - amplopReturned, 6750m, 20250m, false),
            new ReturnSourceLine(KantongLine, 2, Samples.KantongId, "5113", "2_HD kantong hitam 3kg", "BUNGKUS", 2m, 0m, 2m, 4500m, 9000m, false),
        ]);
}
public static class Receipts
{
    public static ReceiptService Service(
        INodeApi api, IDialogService dialogs, FakePrinter? printer = null, string? printerName = "EPSON TM-T82II Receipt",
        int width = 48, ILastSaleStore? lastSale = null) =>
        new(api, printer ?? new FakePrinter(), dialogs, new ReceiptPrinterSettings(printerName, width), lastSale ?? new MemoryLastSaleStore());

    /// <summary>A stored cash sale as GET /api/v1/sales/{id} returns it (sold 11:05 UTC = 18:05 WIB).</summary>
    public static SaleDetail Detail(Guid id, string number = "TK01-SL-202611-000123", bool cash = true) => new(
        id, Guid.CreateVersion7(), number, new DateTime(2026, 11, 20, 11, 5, 0, DateTimeKind.Utc), "Asia/Jakarta",
        new SaleUser(Samples.UserId, "kasir1", "Kasir Satu"), null,
        19500m, 0m, null, 0m, 19500m, 0m, cash ? 500m : 0m, null, null,
        [
            new SaleDetailLine(1, Samples.AmplopId, Guid.CreateVersion7(), "13589", "2023_amplop lebaran karakter", "PCS", 2m, 7500m, 0m, 15000m),
            new SaleDetailLine(2, Samples.KantongId, Guid.CreateVersion7(), "5113", "2_HD kantong hitam 3kg", "BUNGKUS", 1m, 4500m, 0m, 4500m),
        ],
        [cash
            ? new SaleDetailPayment("CASH", "TUNAI", true, false, 19500m, 20000m, 500m, null)
            : new SaleDetailPayment("QRIS", "QRIS", false, false, 19500m, null, null, "REF1")],
        new ReceiptText("TOKO DEV", "Jl. Merdeka No. 1", null, null, "Terimakasih Atas Kunjungannya", null));
}

public sealed class MemoryPreferenceStore<T>(T initial) : IPreferenceStore<T> where T : class
{
    public T Value { get; set; } = initial;
    public T Load() => Value;
    public void Save(T value) => Value = value;
}

/// <summary>Sample stock data for the admin screens.</summary>
public static class Stock
{
    public static readonly StockLocation Display = new(Guid.Parse("01930000-0000-7000-8000-0000000000a1"), "DISPLAY", "Display", "DISPLAY", true);
    public static readonly StockLocation Storage = new(Guid.Parse("01930000-0000-7000-8000-0000000000a2"), "STORAGE", "Gudang toko", "STORAGE", false);

    public static StockTotalRow Row(string sku, string name, decimal baseQty, StockLocation? location = null, string displayUnit = "BOX",
        decimal conversion = 24m, Guid? productId = null) =>
        new(productId ?? Guid.CreateVersion7(), sku, name, "Minuman", "Merek X", "Rak A", (location ?? Display).Id, (location ?? Display).Code,
            (location ?? Display).Name, "PCS", false, displayUnit, conversion, baseQty);

    public static readonly Guid SaleDocId = Guid.Parse("01930000-0000-7000-8000-0000000000e1");

    /// <summary>
    /// A card with opening 10 and <paramref name="rows"/> movements: SALE -2 (first row, doc <see cref="SaleDocId"/>),
    /// then alternating ADJUSTMENT +5 / SALE -2, one minute apart from 20 Nov 2026 02:00 UTC (09:00 WIB).
    /// </summary>
    public static StockCard Card(int rows)
    {
        var list = new List<StockCardRow>();
        var balance = 10m;
        for (var i = 0; i < rows; i++)
        {
            var sale = i % 2 == 0;
            var qty = sale ? -2m : 5m;
            balance += qty;
            list.Add(new StockCardRow(Guid.CreateVersion7(), new DateTime(2026, 11, 20, 2, i % 60, 0, DateTimeKind.Utc).AddHours(i / 60),
                sale ? "SALE" : "ADJUSTMENT", sale ? "Penjualan" : "Penyesuaian", i == 0 ? SaleDocId : Guid.CreateVersion7(),
                sale ? $"TK01-SL-202611-{i + 1:000000}" : $"TK01-ADJ-202611-{i + 1:000000}", sale ? "Kasir Satu" : "Admin Dev",
                Display.Id, "DISPLAY", sale ? new DateOnly(2027, 6, 1) : null, Math.Max(qty, 0), Math.Max(-qty, 0), balance));
        }
        return new StockCard(Guid.Empty, "AIR-600", "Air mineral 600 ml", "PCS", false, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22),
            "Asia/Jakarta", 10m, list.Sum(r => r.QtyIn), list.Sum(r => r.QtyOut), balance, rows, 0, rows, list);
    }

    public static StockExpiry Expiry(Guid productId, Guid? locationId) => new(
        productId, "AIR-600", "Air mineral 600 ml", "PCS", false, "BOX", 24m, true, new DateOnly(2026, 11, 22), 30,
        [
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2026, 11, 19), 2m, "expired"),
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2026, 12, 2), 30m, "near_expiry"),
            new StockExpiryRow(Display.Id, "DISPLAY", new DateOnly(2027, 6, 1), 48m, "ok"),
        ]);
}

