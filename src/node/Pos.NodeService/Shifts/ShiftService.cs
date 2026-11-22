using Dapper;
using System.Text.Json;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Documents;
using Pos.NodeService.Nodes;
using Pos.NodeService.Sales;
using Pos.NodeService.Sync;
using Pos.Shared.Documents;
using Pos.Shared.Sales;
using Pos.Shared.Sync;

namespace Pos.NodeService.Shifts;

public static class ShiftStatus
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

/// <summary>The cashier comes from the access token and the device from header X-Device-Id.</summary>
public sealed record OpenShiftRequest(decimal OpeningCash);

public sealed record CloseShiftRequest(Guid ShiftId, decimal CountedCash, string? Notes = null);

public sealed record ShiftDto(
    Guid Id,
    Guid NodeId,
    string Number,
    Guid DeviceId,
    Guid CashierId,
    string Status,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    decimal OpeningCash,
    decimal? CashTendered,
    decimal? ChangeGiven,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Difference,
    string? Notes,
    int RowVersion,
    string? CashierName = null);

/// <summary>
/// A shift for "Tutup Shift" and its report. Before closing with blind count the totals and the expected cash are
/// null (the cashier counts first); after closing every figure is the one frozen on the row.
/// </summary>
/// <param name="TimeZone">IANA timezone of the branch (for the printed times).</param>
/// <param name="BlindCount">Setting shift.blind_count: the counted cash is entered before the figures are shown.</param>
/// <param name="Receipt">Receipt header texts (null in the outbox).</param>
public sealed record ShiftSummaryDto(
    Guid Id,
    Guid NodeId,
    string Number,
    Guid DeviceId,
    Guid CashierId,
    string CashierName,
    string Status,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    Guid? ClosedById,
    string? ClosedByName,
    string TimeZone,
    bool BlindCount,
    decimal OpeningCash,
    decimal? CashSales,
    decimal? CreditDownPayments,
    decimal? ReceivableCash,
    decimal? CashExpenses,
    IReadOnlyList<ShiftMethodTotal>? NonCash,
    int? SaleCount,
    int? CancellationCount,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Difference,
    string? Notes,
    int RowVersion,
    ReceiptTextDto? Receipt,
    decimal? CashRefunds = null);

/// <summary>
/// Cashier shifts: one OPEN shift per device. Closing sums the shift's documents and freezes the summary.
/// The cashier is the logged-in user; the device is the one the request came from.
/// </summary>
public sealed class ShiftService(TransactionRunner runner, NodeIdentity nodeIdentity, TimeProvider clock)
{
    /// <summary>Setting key: blind count at shift close (default true).</summary>
    public const string BlindCountSetting = "shift.blind_count";

    public async Task<ShiftDto> OpenAsync(OpenShiftRequest request, Guid deviceId, Guid cashierId, CancellationToken cancellationToken = default)
    {
        if (request.OpeningCash < 0)
            throw ApiException.Unprocessable("invalid_amount", "Opening cash must not be negative.");

        var node = await nodeIdentity.GetAsync(cancellationToken);

        return await runner.RunAsync(async (c, tx, ct) =>
        {
            // Locking the device row serialises shift opening per device.
            // (DeviceFilter already checked the device is an ACTIVE device of this node.)
            var device = await c.QuerySingleOrDefaultAsync<(Guid NodeId, string Status)?>(new CommandDefinition(
                "SELECT node_id, status FROM devices WHERE id = @deviceId AND deleted_at IS NULL FOR UPDATE",
                new { deviceId }, tx, cancellationToken: ct));
            if (device is null || device.Value.NodeId != node.Id || device.Value.Status != "ACTIVE")
                throw new ApiException(StatusCodes.Status403Forbidden, "device_not_allowed",
                    $"Device {deviceId} is not an active device of node {node.Code}.");

            var existing = await c.QuerySingleOrDefaultAsync<(Guid Id, string Number)?>(new CommandDefinition(
                "SELECT id, number FROM cashier_shifts WHERE device_id = @deviceId AND status = @open",
                new { deviceId, open = ShiftStatus.Open }, tx, cancellationToken: ct));
            if (existing is not null)
                throw ApiException.Conflict("shift_already_open",
                    $"Device already has open shift {existing.Value.Number}.",
                    new Dictionary<string, object?> { ["shift_id"] = existing.Value.Id, ["shift_number"] = existing.Value.Number });

            var now = clock.GetUtcNow().UtcDateTime;
            var id = Guid.CreateVersion7();
            var number = await DocumentNumbering.NextAsync(c, tx, node, DocumentTypes.CashierShift, now, ct);

            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO cashier_shifts (id, node_id, number, device_id, cashier_id, opened_at, opening_cash, status, row_version)
                VALUES (@id, @nodeId, @number, @deviceId, @cashierId, @now, @OpeningCash, @open, 1)
                """,
                new { id, nodeId = node.Id, number, deviceId, cashierId, now, request.OpeningCash, open = ShiftStatus.Open },
                tx, cancellationToken: ct));

            var dto = new ShiftDto(id, node.Id, number, deviceId, cashierId, ShiftStatus.Open, now, null,
                request.OpeningCash, null, null, null, null, null, null, 1);
            await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.CashierShift, id, dto.RowVersion, ct);
            return dto;
        }, "Open shift", cancellationToken);
    }

    /// <summary>The device's open shift, or 404 <c>no_open_shift</c>.</summary>
    public async Task<ShiftDto> GetCurrentAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var node = await nodeIdentity.GetAsync(cancellationToken);
        await using var c = await runner.ConnectionFactory.OpenAsync(cancellationToken);
        var shift = await c.QuerySingleOrDefaultAsync<ShiftRow>(new CommandDefinition(
            """
            SELECT id AS Id, node_id AS NodeId, number AS Number, device_id AS DeviceId, cashier_id AS CashierId,
                   status AS Status, opened_at AS OpenedAt, opening_cash AS OpeningCash, row_version AS RowVersion
            FROM cashier_shifts WHERE device_id = @deviceId AND node_id = @nodeId AND status = @open
            """, new { deviceId, nodeId = node.Id, open = ShiftStatus.Open }, cancellationToken: cancellationToken))
            ?? throw ApiException.NotFound("no_open_shift", $"Device {deviceId} has no open shift.");

        var cashierName = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT full_name FROM users WHERE id = @CashierId", new { shift.CashierId }, cancellationToken: cancellationToken));
        return new ShiftDto(shift.Id, shift.NodeId, shift.Number, shift.DeviceId, shift.CashierId, shift.Status,
            shift.OpenedAt, null, shift.OpeningCash, null, null, null, null, null, null, shift.RowVersion, cashierName);
    }

    /// <summary>
    /// "Tutup Shift" form data: the shift header and the blind-count setting. Without blind count the running summary
    /// (expected cash) is included; with blind count it stays hidden until the counted cash is saved.
    /// The cashier of the shift, or a user with <c>shift.close</c>.
    /// </summary>
    public async Task<ShiftSummaryDto> GetClosingAsync(Guid shiftId, Guid userId, bool canCloseOthers, CancellationToken cancellationToken = default)
    {
        var node = await nodeIdentity.GetAsync(cancellationToken);
        await using var c = await runner.ConnectionFactory.OpenAsync(cancellationToken);
        var shift = await LoadRowAsync(c, null, shiftId, forUpdate: false, cancellationToken);
        if (shift is null || shift.NodeId != node.Id)
            throw ApiException.NotFound("unknown_shift", $"Shift {shiftId} does not exist at node {node.Code}.");
        EnsureMayClose(shift, userId, canCloseOthers);

        var blind = await BlindCountAsync(c, null, node.BranchId, cancellationToken);
        var receipt = await SaleDetailService.LoadReceiptTextAsync(c, node.BranchId, node.Name, cancellationToken);
        if (shift.Status != ShiftStatus.Open)
            return ToDto(shift, node, blind, receipt);

        if (blind)
            return ToDto(shift, node, blind, receipt);
        var totals = await TotalsAsync(c, null, shift.Id, shift.OpeningCash, cancellationToken);
        return ToDto(shift, node, blind, receipt) with
        {
            CashSales = totals.CashSales,
            CreditDownPayments = totals.CreditDownPayments,
            ReceivableCash = totals.ReceivableCash,
            CashExpenses = totals.CashExpenses,
            CashRefunds = totals.CashRefunds,
            NonCash = totals.NonCash,
            SaleCount = totals.SaleCount,
            CancellationCount = totals.CancellationCount,
            ExpectedCash = ShiftClosing.ExpectedCash(totals),
        };
    }

    /// <summary>
    /// Closes the shift with the counted cash: sums its documents, freezes the summary on the row, writes the outbox
    /// (row_version + 1). The cashier closes their own shift; another user needs <c>shift.close</c> and notes.
    /// Idempotent: the same user sending the same counted cash for an already closed shift gets the stored summary.
    /// </summary>
    public async Task<ShiftSummaryDto> CloseAsync(CloseShiftRequest request, Guid userId, bool canCloseOthers, CancellationToken cancellationToken = default)
    {
        if (request.CountedCash < 0)
            throw ApiException.Unprocessable("invalid_amount", "Counted cash must not be negative.");
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        var node = await nodeIdentity.GetAsync(cancellationToken);

        return await runner.RunAsync(async (c, tx, ct) =>
        {
            // FOR UPDATE waits for sales of this shift that hold a shared lock on it, so the totals are final.
            var shift = await LoadRowAsync(c, tx, request.ShiftId, forUpdate: true, ct);
            if (shift is null || shift.NodeId != node.Id)
                throw ApiException.NotFound("unknown_shift", $"Shift {request.ShiftId} does not exist at node {node.Code}.");

            var blind = await BlindCountAsync(c, tx, node.BranchId, ct);
            var receipt = await SaleDetailService.LoadReceiptTextAsync(c, node.BranchId, node.Name, ct, tx);
            if (shift.Status != ShiftStatus.Open)
            {
                if (shift.ClosedBy == userId && shift.CountedCash == request.CountedCash)
                    return ToDto(shift, node, blind, receipt);                     // retry of the same close
                throw ApiException.Conflict("shift_already_closed", $"Shift {shift.Number} is already closed.");
            }

            EnsureMayClose(shift, userId, canCloseOthers);
            if (shift.CashierId != userId && notes is null)
                throw ApiException.Unprocessable("notes_required",
                    $"Shift {shift.Number} belongs to {shift.CashierName}; closing another cashier's shift needs notes.");

            var totals = await TotalsAsync(c, tx, shift.Id, shift.OpeningCash, ct);
            var expected = ShiftClosing.ExpectedCash(totals);
            var now = clock.GetUtcNow().UtcDateTime;
            var rowVersion = shift.RowVersion + 1;

            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cashier_shifts
                SET status = @closed, closed_at = @now, closed_by = @userId,
                    cash_sales = @CashSales, credit_down_payments = @CreditDownPayments, receivable_cash = @ReceivableCash,
                    cash_expenses = @CashExpenses, cash_refunds = @CashRefunds, non_cash_totals = @nonCash, sale_count = @SaleCount,
                    cancellation_count = @CancellationCount, expected_cash = @expected, counted_cash = @CountedCash,
                    notes = @notes, row_version = @rowVersion
                WHERE id = @Id AND status = @open
                """,
                new
                {
                    closed = ShiftStatus.Closed, now, userId, totals.CashSales, totals.CreditDownPayments, totals.ReceivableCash,
                    totals.CashExpenses, totals.CashRefunds, nonCash = JsonSerializer.Serialize(totals.NonCash, PosJson.Options), totals.SaleCount,
                    totals.CancellationCount, expected, request.CountedCash, notes, rowVersion, shift.Id, open = ShiftStatus.Open,
                },
                tx, cancellationToken: ct));

            var closed = await LoadRowAsync(c, tx, shift.Id, forUpdate: false, ct)
                         ?? throw new InvalidOperationException($"Shift {shift.Id} disappeared while closing.");
            var dto = ToDto(closed, node, blind, receipt);
            await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.CashierShift, shift.Id, rowVersion, ct);
            return dto;
        }, "Close shift", cancellationToken);
    }

    private static void EnsureMayClose(ShiftRow shift, Guid userId, bool canCloseOthers)
    {
        if (shift.CashierId != userId && !canCloseOthers)
            throw new ApiException(StatusCodes.Status403Forbidden, "shift_close_forbidden",
                $"Shift {shift.Number} belongs to {shift.CashierName}; closing another cashier's shift needs permission shift.close.");
    }

    /// <summary>Sums the shift's documents. Voided sales are left out of the money and the nota count.</summary>
    private static async Task<ShiftTotals> TotalsAsync(
        MySqlConnection c, MySqlTransaction? tx, Guid shiftId, decimal openingCash, CancellationToken ct)
    {
        const string NotVoided = "NOT EXISTS (SELECT 1 FROM sale_voids v WHERE v.sale_id = s.id)";

        var cash = await c.QuerySingleAsync<CashRow>(new CommandDefinition(
            $"""
            SELECT
              COALESCE(SUM(CASE WHEN s.credit_amount = 0 THEN sp.amount ELSE 0 END), 0) AS CashSales,
              COALESCE(SUM(CASE WHEN s.credit_amount <> 0 THEN sp.amount ELSE 0 END), 0) AS CreditDownPayments
            FROM sales s
            JOIN sale_payments sp ON sp.sale_id = s.id
            JOIN payment_methods pm ON pm.id = sp.payment_method_id
            WHERE s.shift_id = @shiftId AND pm.is_cash = 1 AND {NotVoided}
            """, new { shiftId }, tx, cancellationToken: ct));

        var nonCash = (await c.QueryAsync<MethodRow>(new CommandDefinition(
            $"""
            SELECT pm.code AS Code, pm.name AS Name, COALESCE(SUM(x.amount), 0) AS Amount, COUNT(DISTINCT x.sale_id) AS Count
            FROM payment_methods pm
            LEFT JOIN (
              SELECT sp.payment_method_id, sp.amount, sp.sale_id
              FROM sale_payments sp JOIN sales s ON s.id = sp.sale_id
              WHERE s.shift_id = @shiftId AND {NotVoided}
            ) x ON x.payment_method_id = pm.id
            WHERE pm.is_cash = 0 AND ((pm.is_active = 1 AND pm.deleted_at IS NULL AND pm.code <> 'RETURN') OR x.sale_id IS NOT NULL)
            GROUP BY pm.id, pm.code, pm.name, pm.is_credit
            ORDER BY pm.is_credit, FIELD(pm.code, 'DEBIT', 'QRIS', 'TRANSFER', 'RETURN') = 0, FIELD(pm.code, 'DEBIT', 'QRIS', 'TRANSFER', 'RETURN'), pm.name
            """, new { shiftId }, tx, cancellationToken: ct)))
            .Select(m => new ShiftMethodTotal(m.Code, m.Name, m.Amount, (int)m.Count)).ToList();

        var other = await c.QuerySingleAsync<OtherRow>(new CommandDefinition(
            $"""
            SELECT
              (SELECT COALESCE(SUM(rp.amount), 0) FROM receivable_payments rp
                 JOIN payment_methods pm ON pm.id = rp.payment_method_id
                 WHERE rp.shift_id = @shiftId AND pm.is_cash = 1) AS ReceivableCash,
              (SELECT COALESCE(SUM(amount), 0) FROM cash_expenses WHERE shift_id = @shiftId) AS CashExpenses,
              (SELECT COALESCE(SUM(refund_amount), 0) FROM sale_returns WHERE shift_id = @shiftId) AS CashRefunds,
              (SELECT COUNT(*) FROM sales s WHERE s.shift_id = @shiftId AND {NotVoided}) AS SaleCount,
              (SELECT COUNT(*) FROM cart_cancellations WHERE shift_id = @shiftId)
                + (SELECT COUNT(*) FROM sale_voids v JOIN sales s ON s.id = v.sale_id WHERE s.shift_id = @shiftId) AS CancellationCount
            """, new { shiftId }, tx, cancellationToken: ct));

        return new ShiftTotals(openingCash, cash.CashSales, cash.CreditDownPayments, other.ReceivableCash, other.CashExpenses,
            nonCash, (int)other.SaleCount, (int)other.CancellationCount, other.CashRefunds);
    }

    /// <summary>Setting shift.blind_count (branch row wins): JSON true/false (or "true"/"false", 1/0); default true.</summary>
    private static async Task<bool> BlindCountAsync(MySqlConnection c, MySqlTransaction? tx, Guid branchId, CancellationToken ct)
    {
        var json = await c.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT CAST(setting_value AS CHAR) FROM settings
            WHERE setting_key = @key AND deleted_at IS NULL AND (branch_id = @branchId OR branch_id IS NULL)
            ORDER BY branch_id IS NULL LIMIT 1
            """, new { key = BlindCountSetting, branchId }, tx, cancellationToken: ct));
        if (json is null)
            return true;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => doc.RootElement.GetDecimal() != 0,
            JsonValueKind.String => !string.Equals(doc.RootElement.GetString(), "false", StringComparison.OrdinalIgnoreCase)
                                    && doc.RootElement.GetString() != "0",
            _ => true,
        };
    }

    private static async Task<ShiftRow?> LoadRowAsync(MySqlConnection c, MySqlTransaction? tx, Guid shiftId, bool forUpdate, CancellationToken ct) =>
        await c.QuerySingleOrDefaultAsync<ShiftRow>(new CommandDefinition(
            $"""
            SELECT cs.id AS Id, cs.node_id AS NodeId, cs.number AS Number, cs.device_id AS DeviceId, cs.cashier_id AS CashierId,
                   u.full_name AS CashierName, cs.status AS Status, cs.opened_at AS OpenedAt, cs.closed_at AS ClosedAt,
                   cs.closed_by AS ClosedBy, cb.full_name AS ClosedByName, cs.opening_cash AS OpeningCash,
                   cs.cash_sales AS CashSales, cs.credit_down_payments AS CreditDownPayments, cs.receivable_cash AS ReceivableCash,
                   cs.cash_expenses AS CashExpenses, cs.cash_refunds AS CashRefunds, CAST(cs.non_cash_totals AS CHAR) AS NonCashJson, cs.sale_count AS SaleCount,
                   cs.cancellation_count AS CancellationCount, cs.expected_cash AS ExpectedCash, cs.counted_cash AS CountedCash,
                   cs.notes AS Notes, cs.row_version AS RowVersion
            FROM cashier_shifts cs
            JOIN users u ON u.id = cs.cashier_id
            LEFT JOIN users cb ON cb.id = cs.closed_by
            WHERE cs.id = @shiftId {(forUpdate ? "FOR UPDATE OF cs" : "")}
            """, new { shiftId }, tx, cancellationToken: ct));

    private static ShiftSummaryDto ToDto(ShiftRow s, NodeInfo node, bool blind, ReceiptTextDto receipt) => new(
        s.Id, s.NodeId, s.Number, s.DeviceId, s.CashierId, s.CashierName, s.Status,
        DateTime.SpecifyKind(s.OpenedAt, DateTimeKind.Utc), s.ClosedAt is { } closedAt ? DateTime.SpecifyKind(closedAt, DateTimeKind.Utc) : null,
        s.ClosedBy, s.ClosedByName, node.TimeZone, blind, s.OpeningCash,
        s.CashSales, s.CreditDownPayments, s.ReceivableCash, s.CashExpenses,
        s.NonCashJson is null ? null : JsonSerializer.Deserialize<List<ShiftMethodTotal>>(s.NonCashJson, PosJson.Options),
        s.SaleCount, s.CancellationCount, s.ExpectedCash, s.CountedCash,
        s.CountedCash is { } counted && s.ExpectedCash is { } expected ? ShiftClosing.Difference(counted, expected) : null,
        s.Notes, s.RowVersion, receipt, s.CashRefunds);


    private sealed class ShiftRow
    {
        public Guid Id { get; set; }
        public Guid NodeId { get; set; }
        public string Number { get; set; } = "";
        public Guid DeviceId { get; set; }
        public Guid CashierId { get; set; }
        public string CashierName { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public Guid? ClosedBy { get; set; }
        public string? ClosedByName { get; set; }
        public decimal OpeningCash { get; set; }
        public decimal? CashSales { get; set; }
        public decimal? CreditDownPayments { get; set; }
        public decimal? ReceivableCash { get; set; }
        public decimal? CashExpenses { get; set; }
        public decimal? CashRefunds { get; set; }
        public string? NonCashJson { get; set; }
        public int? SaleCount { get; set; }
        public int? CancellationCount { get; set; }
        public decimal? ExpectedCash { get; set; }
        public decimal? CountedCash { get; set; }
        public string? Notes { get; set; }
        public int RowVersion { get; set; }
    }

    private sealed class CashRow
    {
        public decimal CashSales { get; set; }
        public decimal CreditDownPayments { get; set; }
    }

    private sealed class MethodRow
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public long Count { get; set; }
    }

    private sealed class OtherRow
    {
        public decimal ReceivableCash { get; set; }
        public decimal CashExpenses { get; set; }
        public decimal CashRefunds { get; set; }
        public long SaleCount { get; set; }
        public long CancellationCount { get; set; }
    }
}
