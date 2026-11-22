using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Auth;
using Pos.NodeService.Data;
using Pos.NodeService.Documents;
using Pos.NodeService.Nodes;
using Pos.NodeService.Shifts;
using Pos.NodeService.Stock;
using Pos.NodeService.Sync;
using Pos.Shared.Auth;
using Pos.Shared.Documents;
using Pos.Shared.Sales;
using Pos.Shared.Stock;
using Pos.Shared.Sync;
using Pos.Shared.Time;

namespace Pos.NodeService.Sales;

public static class ReturnConditions
{
    /// <summary>BAIK: back to the location it was sold from.</summary>
    public const string Good = "GOOD";

    /// <summary>RUSAK: to the node's DAMAGED location.</summary>
    public const string Damaged = "DAMAGED";
}

/// <summary>A nota line as the return form needs it: what was sold, what is already returned, the net price.</summary>
public sealed record ReturnSourceLineDto(
    Guid SaleLineId, int LineNo, Guid ProductId, string Sku, string Name, string UnitCode, decimal SoldQty, decimal ReturnedQty,
    decimal ReturnableQty, decimal NetUnitPrice, decimal NetLineTotal, bool AllowDecimal);

/// <summary>A nota for the return form (opened by id from Data Penjualan or by the number typed / scanned).</summary>
/// <param name="WithinWindow">Within setting returns.window_days; otherwise a supervisor (sale.return.late) approves.</param>
public sealed record ReturnSourceDto(
    Guid SaleId, string SaleNumber, DateTime SoldAt, string TimeZone, DateOnly SaleDate, string? CustomerCode, string? CustomerName,
    string CashierName, decimal Total, decimal CreditAmount, decimal Outstanding, bool Voided, int WindowDays, bool WithinWindow,
    int CurrencyDecimals, IReadOnlyList<ReturnSourceLineDto> Lines);

/// <param name="Condition">GOOD (BAIK) or DAMAGED (RUSAK).</param>
public sealed record SaleReturnLineRequest(Guid SaleLineId, decimal Qty, string Condition);

/// <param name="Id">UUID v7 from the client (a retry with the same id returns the stored return).</param>
/// <param name="Settlement">REFUND, EXCHANGE or REDUCE_RECEIVABLE.</param>
/// <param name="ApprovalToken">Supervisor approval (sale.return.late) after the return window.</param>
public sealed record CreateSaleReturnRequest(
    Guid Id, Guid SaleId, string Settlement, Guid ReasonId, string? Notes, IReadOnlyList<SaleReturnLineRequest>? Lines,
    string? ApprovalToken = null);

public sealed record ReturnExpiryDto(DateOnly? ExpiryDate, decimal BaseQty);

public sealed record SaleReturnLineDto(
    int LineNo, Guid SaleLineId, Guid ProductId, string Sku, string Name, Guid ProductUnitId, string UnitCode, decimal Qty, decimal BaseQty,
    decimal UnitPrice, decimal LineTotal, string Condition, Guid LocationId, string LocationCode, IReadOnlyList<ReturnExpiryDto> Expiries);

/// <param name="ExchangeSaleId">TUKAR BARANG: the sale its value paid (null while unused).</param>
public sealed record SaleReturnDto(
    Guid Id, Guid NodeId, string Number, Guid SaleId, string SaleNumber, Guid? ShiftId, Guid? DeviceId, DateTime ReturnedAt, string TimeZone,
    string Settlement, decimal Total, decimal RefundAmount, Guid ReasonId, string ReasonName, string? Notes,
    Guid CreatedBy, string CreatedByName, Guid? ApprovedBy, string? ApprovedByName, string? CustomerCode, string? CustomerName,
    Guid? ExchangeSaleId, string? ExchangeSaleNumber, IReadOnlyList<SaleReturnLineDto> Lines, ReceiptTextDto? Receipt);

public sealed record SaleReturnResult(SaleReturnDto Return, bool Replayed);

public sealed record SaleReturnSummaryDto(
    Guid Id, string Number, DateTime ReturnedAt, string SaleNumber, string? CustomerName, string CreatedByName, string Settlement,
    decimal Total, string? Notes, bool ExchangeUsed, string TimeZone);

/// <summary>
/// Retur penjualan (10c). Quantities per nota line up to sold minus already returned; BAIK goes back to the location
/// it was sold from, RUSAK to the node's DAMAGED location, both into the expiry dates the sale took (earliest first).
/// Refund price = net price on the nota (<see cref="ReturnPricing"/>). After returns.window_days a supervisor approves.
/// UANG KEMBALI needs the device's open shift (it reduces that drawer's expected cash); POTONG PIUTANG only for credit
/// notas; TUKAR BARANG is used once to pay a new sale. Own-node notas only for now (replicas from other stores later).
/// </summary>
public sealed class SaleReturnService(TransactionRunner runner, NodeIdentity nodeIdentity, TimeProvider clock)
{
    public const string WindowDaysSetting = "returns.window_days";
    public const int DefaultWindowDays = 7;

    // ---- source -----------------------------------------------------------------------------------

    public async Task<ReturnSourceDto> GetSourceAsync(Guid? saleId, string? number, CancellationToken ct = default)
    {
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await runner.ConnectionFactory.OpenAsync(ct);
        var id = saleId ?? await c.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM sales WHERE number = @number", new { number = (number ?? "").Trim() }, cancellationToken: ct))
            ?? throw ApiException.NotFound("unknown_sale", $"Nota {number} does not exist at this node.");
        var sale = await LoadSaleAsync(c, null, id, forUpdate: false, ct)
                   ?? throw ApiException.NotFound("unknown_sale", $"Sale {id} does not exist at this node.");
        if (sale.NodeId != node.Id)
            throw ApiException.Unprocessable("foreign_sale", "Returns of notas from other stores are not available yet.");

        var lines = await LoadLinesAsync(c, null, id, ct);
        var settings = await SaleSettings.LoadAsync(c, null, node.BranchId, ct);
        var window = await SettingsReader.GetIntAsync(c, null, WindowDaysSetting, node.BranchId, DefaultWindowDays, ct);
        var saleDate = BranchClock.Today(DateTime.SpecifyKind(sale.SoldAt, DateTimeKind.Utc), node.TimeZone);
        var today = BranchClock.Today(clock.GetUtcNow().UtcDateTime, node.TimeZone);

        return new ReturnSourceDto(sale.Id, sale.Number, DateTime.SpecifyKind(sale.SoldAt, DateTimeKind.Utc), node.TimeZone, saleDate,
            sale.CustomerCode, sale.CustomerName, sale.CashierName, sale.Total, sale.CreditAmount, Outstanding(sale), sale.Voided != 0,
            window, ReturnPricing.WithinWindow(saleDate, today, window), settings.CurrencyDecimals,
            lines.Select(l =>
            {
                var net = ReturnPricing.NetLineTotal(l.LineTotal, sale.Subtotal, sale.DiscountAmount);
                return new ReturnSourceLineDto(l.Id, l.LineNo, l.ProductId, l.Sku, l.Name, l.UnitCode, l.Qty, l.ReturnedQty,
                    ReturnPricing.Returnable(l.Qty, l.ReturnedQty), ReturnPricing.NetUnitPrice(net, l.Qty), net, l.AllowDecimal);
            }).ToList());
    }

    // ---- create -----------------------------------------------------------------------------------

    public async Task<SaleReturnResult> CreateAsync(
        CreateSaleReturnRequest request, Guid deviceId, Guid userId, bool canReturnLate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id.Version != 7)
            throw ApiException.Unprocessable("invalid_id", "The return id must be a UUID v7 generated by the client.");
        if (request.Lines is not { Count: > 0 } lines)
            throw ApiException.Unprocessable("no_lines", "A return needs at least one line.");
        if (lines.Select(l => l.SaleLineId).Distinct().Count() != lines.Count)
            throw ApiException.Unprocessable("duplicate_line", "Each nota line may appear once in a return.");
        if (lines.Any(l => l.Condition is not (ReturnConditions.Good or ReturnConditions.Damaged)))
            throw ApiException.Unprocessable("invalid_condition", "Condition must be GOOD or DAMAGED.");
        if (!ReturnSettlements.All.Contains(request.Settlement))
            throw ApiException.Unprocessable("invalid_settlement", $"Unknown settlement {request.Settlement}.");

        var node = await nodeIdentity.GetAsync(cancellationToken);
        if (await TryLoadAsync(request.Id, cancellationToken) is { } stored)
            return new SaleReturnResult(stored, Replayed: true);
        try
        {
            return await runner.RunAsync((c, tx, ct) => CreateWithinAsync(c, tx, node, request, lines, deviceId, userId, canReturnLate, ct),
                "Create sale return", cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            stored = await TryLoadAsync(request.Id, cancellationToken);
            if (stored is null)
                throw;
            return new SaleReturnResult(stored, Replayed: true);
        }
    }

    private async Task<SaleReturnResult> CreateWithinAsync(
        MySqlConnection c, MySqlTransaction tx, NodeInfo node, CreateSaleReturnRequest request, IReadOnlyList<SaleReturnLineRequest> lines,
        Guid deviceId, Guid userId, bool canReturnLate, CancellationToken ct)
    {
        // The sale row lock serialises returns of one nota, so "already returned" is exact.
        var sale = await LoadSaleAsync(c, tx, request.SaleId, forUpdate: true, ct)
                   ?? throw ApiException.NotFound("unknown_sale", $"Sale {request.SaleId} does not exist at this node.");
        if (sale.NodeId != node.Id)
            throw ApiException.Unprocessable("foreign_sale", "Returns of notas from other stores are not available yet.");
        // A concurrent request with the same id may have committed while this one waited for the lock.
        if (await LoadAsync(c, tx, request.Id, ct) is { } concurrent)
            return new SaleReturnResult(concurrent, Replayed: true);
        if (sale.Voided != 0)
            throw ApiException.Conflict("sale_voided", $"Sale {sale.Number} is voided.");
        if (await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM reasons WHERE id = @ReasonId AND type = @type AND deleted_at IS NULL",
                new { request.ReasonId, type = ReasonTypes.Return }, tx, cancellationToken: ct)) == 0)
            throw ApiException.Unprocessable("unknown_reason", "The reason is not a return reason (RETURN).");

        var settings = await SaleSettings.LoadAsync(c, tx, node.BranchId, ct);
        var saleLines = (await LoadLinesAsync(c, tx, sale.Id, ct)).ToDictionary(l => l.Id);
        var now = clock.GetUtcNow().UtcDateTime;
        var priced = new List<(SaleReturnLineRequest Request, LineRow Line, decimal LineTotal, decimal UnitPrice)>();
        foreach (var line in lines)
        {
            if (!saleLines.TryGetValue(line.SaleLineId, out var saleLine))
                throw ApiException.Unprocessable("unknown_sale_line", $"Line {line.SaleLineId} is not on nota {sale.Number}.");
            if (!saleLine.AllowDecimal && decimal.Truncate(line.Qty) != line.Qty)
                throw ApiException.Unprocessable("invalid_return_qty", $"{saleLine.Name}: whole {saleLine.UnitCode} only.");
            var net = ReturnPricing.NetLineTotal(saleLine.LineTotal, sale.Subtotal, sale.DiscountAmount);
            var amount = Validate(() => ReturnPricing.RefundAmount(net, saleLine.Qty, saleLine.ReturnedQty, line.Qty, settings.CurrencyDecimals));
            priced.Add((line, saleLine, amount, ReturnPricing.NetUnitPrice(net, saleLine.Qty)));
        }
        var total = priced.Sum(p => p.LineTotal);
        Validate(() => { ReturnSettlements.Validate(request.Settlement, total, sale.CreditAmount, Outstanding(sale)); return 0; });

        // After the return window a supervisor approves (or the user has sale.return.late).
        Guid? approvedBy = null;
        var window = await SettingsReader.GetIntAsync(c, tx, WindowDaysSetting, node.BranchId, DefaultWindowDays, ct);
        var saleDate = BranchClock.Today(DateTime.SpecifyKind(sale.SoldAt, DateTimeKind.Utc), node.TimeZone);
        if (!ReturnPricing.WithinWindow(saleDate, BranchClock.Today(now, node.TimeZone), window) && !canReturnLate)
        {
            if (string.IsNullOrWhiteSpace(request.ApprovalToken))
                throw new ApiException(StatusCodes.Status403Forbidden, "return_approval_required",
                    $"Nota {sale.Number} is older than {window} days; a supervisor must approve the return.",
                    new Dictionary<string, object?> { ["window_days"] = window });
            approvedBy = await ApprovalTokens.ConsumeAsync(c, tx, request.ApprovalToken, Permissions.SaleReturnLate, userId, request.Id, now, ct)
                ?? throw new ApiException(StatusCodes.Status403Forbidden, "invalid_approval",
                    "The approval token is unknown, expired, already used, or not for this action or user.");
        }

        // UANG KEMBALI comes out of the drawer of the open shift on this device (a close waits for this return).
        var shift = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM cashier_shifts WHERE device_id = @deviceId AND node_id = @nodeId AND status = @open FOR SHARE",
            new { deviceId, nodeId = node.Id, open = ShiftStatus.Open }, tx, cancellationToken: ct));
        if (request.Settlement == ReturnSettlements.Refund && shift is null)
            throw ApiException.Conflict("no_open_shift", "A cash refund needs an open shift on this device.");

        Guid? damagedLocation = null;
        if (priced.Any(p => p.Request.Condition == ReturnConditions.Damaged))
        {
            damagedLocation = await c.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                """
                SELECT id FROM locations WHERE node_id = @nodeId AND type = 'DAMAGED' AND is_active = 1 AND deleted_at IS NULL
                ORDER BY code LIMIT 1
                """, new { nodeId = node.Id }, tx, cancellationToken: ct))
                ?? throw ApiException.Unprocessable("no_damaged_location", $"Node {node.Code} has no DAMAGED location for damaged returns.");
        }

        var number = await DocumentNumbering.NextAsync(c, tx, node, DocumentTypes.SaleReturn, now, ct);
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sale_returns (id, node_id, number, sale_id, shift_id, device_id, returned_at, settlement, total, refund_amount,
                                      notes, approved_by, created_by)
            VALUES (@Id, @nodeId, @number, @saleId, @shift, @deviceId, @now, @Settlement, @total, @refund, @notes, @approvedBy, @userId)
            """,
            new
            {
                request.Id, nodeId = node.Id, number, saleId = sale.Id, shift, deviceId, now, request.Settlement, total,
                refund = request.Settlement == ReturnSettlements.Refund ? total : 0m, notes, approvedBy, userId,
            }, tx, cancellationToken: ct));

        var movements = new List<StockMovement>();
        var lineNo = 0;
        foreach (var (line, saleLine, lineTotal, unitPrice) in priced)
        {
            var returnLineId = Guid.CreateVersion7();
            var baseQty = line.Qty * saleLine.Conversion;
            var location = line.Condition == ReturnConditions.Damaged ? damagedLocation!.Value : saleLine.LocationId;
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sale_return_lines (id, sale_return_id, line_no, sale_line_id, product_id, product_unit_id, qty, base_qty,
                                               unit_price, line_total, item_condition, location_id, expiry_date, reason_id)
                VALUES (@returnLineId, @returnId, @lineNo, @saleLineId, @ProductId, @ProductUnitId, @Qty, @baseQty,
                        @unitPrice, @lineTotal, @Condition, @location, NULL, @ReasonId)
                """,
                new
                {
                    returnLineId, returnId = request.Id, lineNo = ++lineNo, saleLineId = saleLine.Id, saleLine.ProductId,
                    saleLine.ProductUnitId, line.Qty, baseQty, unitPrice, lineTotal, line.Condition, location, request.ReasonId,
                }, tx, cancellationToken: ct));

            // Into the expiry dates the sale took, earliest first, minus what earlier returns of this line took back.
            var sold = await BucketsAsync(c, tx,
                "SELECT expiry_date AS ExpiryDate, -base_qty AS BaseQty FROM stock_ledger WHERE doc_type = 'SALE' AND doc_id = @saleId AND doc_line_id = @lineId AND base_qty < 0",
                new { saleId = sale.Id, lineId = saleLine.Id }, ct);
            var returned = await BucketsAsync(c, tx,
                """
                SELECT sl.expiry_date AS ExpiryDate, sl.base_qty AS BaseQty FROM stock_ledger sl
                JOIN sale_return_lines srl ON srl.id = sl.doc_line_id
                WHERE sl.doc_type = 'SALE_RETURN' AND srl.sale_line_id = @lineId AND srl.id <> @returnLineId AND sl.base_qty > 0
                """, new { lineId = saleLine.Id, returnLineId }, ct);
            var buckets = sold.Count == 0
                ? [new StockAllocation(null, baseQty)]                                  // no ledger (should not happen): no expiry
                : StockReversal.AllocateReturn(sold, returned, Math.Min(baseQty, sold.Sum(b => b.BaseQty) - returned.Sum(b => b.BaseQty)));
            movements.AddRange(buckets.Select(b => new StockMovement(
                new StockDocumentLine(node.Id, location, saleLine.ProductId, StockDocTypes.SaleReturn, request.Id, returnLineId, now),
                StockDirection.In, b.BaseQty, b.ExpiryDate)));
        }
        await StockPostingService.PostWithinAsync(c, tx, movements, ct);

        var dto = await LoadAsync(c, tx, request.Id, ct) ?? throw new InvalidOperationException("The return was not stored.");
        await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.SaleReturn, dto.Id, 1, dto with { Receipt = null }, ct);
        return new SaleReturnResult(dto, Replayed: false);
    }

    private static async Task<IReadOnlyList<StockAllocation>> BucketsAsync(MySqlConnection c, MySqlTransaction tx, string sql, object args, CancellationToken ct) =>
        (await c.QueryAsync<(DateTime? ExpiryDate, decimal BaseQty)>(new CommandDefinition(sql, args, tx, cancellationToken: ct)))
        .Select(r => new StockAllocation(r.ExpiryDate is { } e ? DateOnly.FromDateTime(e) : null, r.BaseQty)).ToList();

    private static T Validate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SaleValidationException ex)
        {
            throw ApiException.Unprocessable(ex.Code, ex.Message);
        }
    }

    // ---- read -------------------------------------------------------------------------------------

    public async Task<SaleReturnDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await TryLoadAsync(id, ct) ?? throw ApiException.NotFound("unknown_return", $"Return {id} does not exist at this node.");

    public async Task<SaleReturnDto?> TryLoadAsync(Guid id, CancellationToken ct)
    {
        await using var c = await runner.ConnectionFactory.OpenAsync(ct);
        return await LoadAsync(c, null, id, ct);
    }

    /// <summary>Data Retur Penjualan: this node's returns in branch dates, newest first.</summary>
    public async Task<IReadOnlyList<SaleReturnSummaryDto>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
            throw ApiException.Unprocessable("invalid_range", "The end date is before the start date.");
        var node = await nodeIdentity.GetAsync(ct);
        var (fromUtc, toUtc) = BranchClock.DayRangeUtc(from, to, TimeZoneInfo.FindSystemTimeZoneById(node.TimeZone));
        await using var c = await runner.ConnectionFactory.OpenAsync(ct);
        var rows = await c.QueryAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT r.id AS Id, r.number AS Number, r.returned_at AS ReturnedAt, s.number AS SaleNumber, ct.name AS CustomerName,
                   u.full_name AS CreatedByName, r.settlement AS Settlement, r.total AS Total, r.notes AS Notes,
                   EXISTS (SELECT 1 FROM sale_payments sp WHERE sp.sale_return_id = r.id) AS ExchangeUsed
            FROM sale_returns r
            JOIN sales s ON s.id = r.sale_id
            JOIN users u ON u.id = r.created_by
            LEFT JOIN contacts ct ON ct.id = s.customer_id
            WHERE r.node_id = @nodeId AND r.returned_at >= @fromUtc AND r.returned_at < @toUtc
            ORDER BY r.returned_at DESC, r.number DESC
            """, new { nodeId = node.Id, fromUtc, toUtc }, cancellationToken: ct));
        return rows.Select(r => new SaleReturnSummaryDto(r.Id, r.Number, DateTime.SpecifyKind(r.ReturnedAt, DateTimeKind.Utc), r.SaleNumber,
            r.CustomerName, r.CreatedByName, r.Settlement, r.Total, r.Notes, r.ExchangeUsed != 0, node.TimeZone)).ToList();
    }

    private static async Task<SaleReturnDto?> LoadAsync(MySqlConnection c, MySqlTransaction? tx, Guid id, CancellationToken ct)
    {
        var h = await c.QuerySingleOrDefaultAsync<HeaderRow>(new CommandDefinition(
            """
            SELECT r.id AS Id, r.node_id AS NodeId, r.number AS Number, r.sale_id AS SaleId, s.number AS SaleNumber, r.shift_id AS ShiftId,
                   r.device_id AS DeviceId, r.returned_at AS ReturnedAt, b.timezone AS TimeZone, b.id AS BranchId, n.name AS NodeName,
                   r.settlement AS Settlement, r.total AS Total, r.refund_amount AS RefundAmount, r.notes AS Notes,
                   r.created_by AS CreatedBy, u.full_name AS CreatedByName, r.approved_by AS ApprovedBy, a.full_name AS ApprovedByName,
                   ct.code AS CustomerCode, ct.name AS CustomerName, xs.id AS ExchangeSaleId, xs.number AS ExchangeSaleNumber
            FROM sale_returns r
            JOIN sales s ON s.id = r.sale_id
            JOIN nodes n ON n.id = r.node_id
            JOIN branches b ON b.id = n.branch_id
            JOIN users u ON u.id = r.created_by
            LEFT JOIN users a ON a.id = r.approved_by
            LEFT JOIN contacts ct ON ct.id = s.customer_id
            LEFT JOIN sale_payments xp ON xp.sale_return_id = r.id
            LEFT JOIN sales xs ON xs.id = xp.sale_id
            WHERE r.id = @id
            """, new { id }, tx, cancellationToken: ct));
        if (h is null)
            return null;

        var lines = (await c.QueryAsync<ReturnLineRow>(new CommandDefinition(
            """
            SELECT srl.id AS Id, srl.line_no AS LineNo, srl.sale_line_id AS SaleLineId, srl.product_id AS ProductId, p.sku AS Sku, p.name AS Name,
                   srl.product_unit_id AS ProductUnitId, un.code AS UnitCode, srl.qty AS Qty, srl.base_qty AS BaseQty,
                   srl.unit_price AS UnitPrice, srl.line_total AS LineTotal, srl.item_condition AS `Condition`,
                   srl.location_id AS LocationId, l.code AS LocationCode, srl.reason_id AS ReasonId, rs.name AS ReasonName
            FROM sale_return_lines srl
            JOIN products p ON p.id = srl.product_id
            JOIN product_units pu ON pu.id = srl.product_unit_id
            JOIN units un ON un.id = pu.unit_id
            JOIN locations l ON l.id = srl.location_id
            LEFT JOIN reasons rs ON rs.id = srl.reason_id
            WHERE srl.sale_return_id = @id ORDER BY srl.line_no
            """, new { id }, tx, cancellationToken: ct))).ToList();
        var expiries = (await c.QueryAsync<(Guid LineId, DateTime? ExpiryDate, decimal BaseQty)>(new CommandDefinition(
            "SELECT doc_line_id, expiry_date, base_qty FROM stock_ledger WHERE doc_type = 'SALE_RETURN' AND doc_id = @id ORDER BY expiry_date IS NULL, expiry_date",
            new { id }, tx, cancellationToken: ct))).ToList();
        var receipt = await SaleDetailService.LoadReceiptTextAsync(c, h.BranchId, h.NodeName, ct, tx);

        return new SaleReturnDto(h.Id, h.NodeId, h.Number, h.SaleId, h.SaleNumber, h.ShiftId, h.DeviceId,
            DateTime.SpecifyKind(h.ReturnedAt, DateTimeKind.Utc), h.TimeZone, h.Settlement, h.Total, h.RefundAmount,
            lines.FirstOrDefault()?.ReasonId ?? Guid.Empty, lines.FirstOrDefault()?.ReasonName ?? "", h.Notes, h.CreatedBy, h.CreatedByName,
            h.ApprovedBy, h.ApprovedByName, h.CustomerCode, h.CustomerName, h.ExchangeSaleId, h.ExchangeSaleNumber,
            lines.Select(l => new SaleReturnLineDto(l.LineNo, l.SaleLineId, l.ProductId, l.Sku, l.Name, l.ProductUnitId, l.UnitCode, l.Qty,
                l.BaseQty, l.UnitPrice, l.LineTotal, l.Condition, l.LocationId, l.LocationCode,
                expiries.Where(e => e.LineId == l.Id)
                    .Select(e => new ReturnExpiryDto(e.ExpiryDate is { } d ? DateOnly.FromDateTime(d) : null, e.BaseQty)).ToList())).ToList(),
            receipt);
    }

    private static decimal Outstanding(SaleRow sale) => SaleBalance.Outstanding(sale.CreditAmount, sale.Paid, sale.Reduced, sale.Voided != 0);

    private static Task<SaleRow?> LoadSaleAsync(MySqlConnection c, MySqlTransaction? tx, Guid saleId, bool forUpdate, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<SaleRow?>(new CommandDefinition(
            $"""
            SELECT s.id AS Id, s.node_id AS NodeId, s.number AS Number, s.sold_at AS SoldAt, ct.code AS CustomerCode, ct.name AS CustomerName,
                   u.full_name AS CashierName, s.subtotal AS Subtotal, s.discount_amount AS DiscountAmount, s.total AS Total,
                   s.credit_amount AS CreditAmount,
                   EXISTS (SELECT 1 FROM sale_voids v WHERE v.sale_id = s.id) AS Voided,
                   COALESCE((SELECT SUM(a.amount) FROM receivable_payment_allocations a WHERE a.sale_id = s.id), 0) AS Paid,
                   COALESCE((SELECT SUM(r.total) FROM sale_returns r WHERE r.sale_id = s.id AND r.settlement = 'REDUCE_RECEIVABLE'), 0) AS Reduced
            FROM sales s
            JOIN users u ON u.id = s.created_by
            LEFT JOIN contacts ct ON ct.id = s.customer_id
            WHERE s.id = @saleId {(forUpdate ? "FOR UPDATE OF s" : "")}
            """, new { saleId }, tx, cancellationToken: ct));

    private static async Task<IReadOnlyList<LineRow>> LoadLinesAsync(MySqlConnection c, MySqlTransaction? tx, Guid saleId, CancellationToken ct) =>
        (await c.QueryAsync<LineRow>(new CommandDefinition(
            """
            SELECT sl.id AS Id, sl.line_no AS LineNo, sl.product_id AS ProductId, sl.product_unit_id AS ProductUnitId, p.sku AS Sku,
                   p.name AS Name, un.code AS UnitCode, un.allow_decimal AS AllowDecimal, sl.location_id AS LocationId, sl.qty AS Qty,
                   sl.conversion AS Conversion, sl.line_total AS LineTotal,
                   COALESCE((SELECT SUM(srl.qty) FROM sale_return_lines srl WHERE srl.sale_line_id = sl.id), 0) AS ReturnedQty
            FROM sale_lines sl
            JOIN products p ON p.id = sl.product_id
            JOIN product_units pu ON pu.id = sl.product_unit_id
            JOIN units un ON un.id = pu.unit_id
            WHERE sl.sale_id = @saleId ORDER BY sl.line_no
            """, new { saleId }, tx, cancellationToken: ct))).ToList();

    private sealed class SaleRow
    {
        public Guid Id { get; set; }
        public Guid NodeId { get; set; }
        public string Number { get; set; } = "";
        public DateTime SoldAt { get; set; }
        public string? CustomerCode { get; set; }
        public string? CustomerName { get; set; }
        public string CashierName { get; set; } = "";
        public decimal Subtotal { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal Total { get; set; }
        public decimal CreditAmount { get; set; }
        public long Voided { get; set; }
        public decimal Paid { get; set; }
        public decimal Reduced { get; set; }
    }

    private sealed class LineRow
    {
        public Guid Id { get; set; }
        public int LineNo { get; set; }
        public Guid ProductId { get; set; }
        public Guid ProductUnitId { get; set; }
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public string UnitCode { get; set; } = "";
        public bool AllowDecimal { get; set; }
        public Guid LocationId { get; set; }
        public decimal Qty { get; set; }
        public decimal Conversion { get; set; }
        public decimal LineTotal { get; set; }
        public decimal ReturnedQty { get; set; }
    }

    private sealed class HeaderRow
    {
        public Guid Id { get; set; }
        public Guid NodeId { get; set; }
        public string Number { get; set; } = "";
        public Guid SaleId { get; set; }
        public string SaleNumber { get; set; } = "";
        public Guid? ShiftId { get; set; }
        public Guid? DeviceId { get; set; }
        public DateTime ReturnedAt { get; set; }
        public string TimeZone { get; set; } = "";
        public Guid BranchId { get; set; }
        public string NodeName { get; set; } = "";
        public string Settlement { get; set; } = "";
        public decimal Total { get; set; }
        public decimal RefundAmount { get; set; }
        public string? Notes { get; set; }
        public Guid CreatedBy { get; set; }
        public string CreatedByName { get; set; } = "";
        public Guid? ApprovedBy { get; set; }
        public string? ApprovedByName { get; set; }
        public string? CustomerCode { get; set; }
        public string? CustomerName { get; set; }
        public Guid? ExchangeSaleId { get; set; }
        public string? ExchangeSaleNumber { get; set; }
    }

    private sealed class ReturnLineRow
    {
        public Guid Id { get; set; }
        public int LineNo { get; set; }
        public Guid SaleLineId { get; set; }
        public Guid ProductId { get; set; }
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public Guid ProductUnitId { get; set; }
        public string UnitCode { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal BaseQty { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
        public string Condition { get; set; } = "";
        public Guid LocationId { get; set; }
        public string LocationCode { get; set; } = "";
        public Guid? ReasonId { get; set; }
        public string? ReasonName { get; set; }
    }

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
        public DateTime ReturnedAt { get; set; }
        public string SaleNumber { get; set; } = "";
        public string? CustomerName { get; set; }
        public string CreatedByName { get; set; } = "";
        public string Settlement { get; set; } = "";
        public decimal Total { get; set; }
        public string? Notes { get; set; }
        public long ExchangeUsed { get; set; }
    }
}
