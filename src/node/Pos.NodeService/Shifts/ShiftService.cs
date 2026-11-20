using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Documents;
using Pos.NodeService.Nodes;
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
    int RowVersion);

/// <summary>
/// Cashier shifts: one OPEN shift per device. Closing computes the expected drawer cash.
/// The cashier is the logged-in user; the device is the one the request came from.
/// </summary>
public sealed class ShiftService(TransactionRunner runner, NodeIdentity nodeIdentity, TimeProvider clock)
{
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
            await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.CashierShift, id, dto.RowVersion, dto, ct);
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

        return new ShiftDto(shift.Id, shift.NodeId, shift.Number, shift.DeviceId, shift.CashierId, shift.Status,
            shift.OpenedAt, null, shift.OpeningCash, null, null, null, null, null, null, shift.RowVersion);
    }

    public async Task<ShiftDto> CloseAsync(CloseShiftRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CountedCash < 0)
            throw ApiException.Unprocessable("invalid_amount", "Counted cash must not be negative.");

        var node = await nodeIdentity.GetAsync(cancellationToken);

        return await runner.RunAsync(async (c, tx, ct) =>
        {
            // FOR UPDATE waits for sales of this shift that hold a shared lock on it, so the totals are final.
            var shift = await c.QuerySingleOrDefaultAsync<ShiftRow>(new CommandDefinition(
                """
                SELECT id AS Id, node_id AS NodeId, number AS Number, device_id AS DeviceId, cashier_id AS CashierId,
                       status AS Status, opened_at AS OpenedAt, opening_cash AS OpeningCash, row_version AS RowVersion
                FROM cashier_shifts WHERE id = @ShiftId FOR UPDATE
                """,
                new { request.ShiftId }, tx, cancellationToken: ct));
            if (shift is null || shift.NodeId != node.Id)
                throw ApiException.NotFound("unknown_shift", $"Shift {request.ShiftId} does not exist at node {node.Code}.");
            if (shift.Status != ShiftStatus.Open)
                throw ApiException.Conflict("shift_already_closed", $"Shift {shift.Number} is already closed.");

            var (tendered, change) = await CashTotalsAsync(c, tx, shift.Id, ct);
            var expected = CashDrawer.ExpectedCash(shift.OpeningCash, tendered, change);
            var now = clock.GetUtcNow().UtcDateTime;
            var rowVersion = shift.RowVersion + 1;

            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cashier_shifts
                SET status = @closed, closed_at = @now, expected_cash = @expected, counted_cash = @CountedCash,
                    notes = @Notes, row_version = @rowVersion
                WHERE id = @Id
                """,
                new { closed = ShiftStatus.Closed, now, expected, request.CountedCash, request.Notes, rowVersion, shift.Id },
                tx, cancellationToken: ct));

            var dto = new ShiftDto(shift.Id, shift.NodeId, shift.Number, shift.DeviceId, shift.CashierId, ShiftStatus.Closed,
                shift.OpenedAt, now, shift.OpeningCash, tendered, change, expected, request.CountedCash,
                CashDrawer.Difference(request.CountedCash, expected), request.Notes, rowVersion);
            await OutboxWriter.WriteAsync(c, tx, SyncDocTypes.CashierShift, shift.Id, rowVersion, dto, ct);
            return dto;
        }, "Close shift", cancellationToken);
    }

    /// <summary>Cash handed over (tendered, or the amount when no tender was recorded) and change, for cash methods.</summary>
    private static async Task<(decimal Tendered, decimal Change)> CashTotalsAsync(
        MySqlConnection c, MySqlTransaction tx, Guid shiftId, CancellationToken ct) =>
        await c.QuerySingleAsync<(decimal, decimal)>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(COALESCE(sp.tendered, sp.amount)), 0), COALESCE(SUM(COALESCE(sp.change_given, 0)), 0)
            FROM sale_payments sp
            JOIN sales s ON s.id = sp.sale_id
            JOIN payment_methods pm ON pm.id = sp.payment_method_id
            WHERE s.shift_id = @shiftId AND pm.is_cash = 1
            """,
            new { shiftId }, tx, cancellationToken: ct));

    private sealed record ShiftRow(
        Guid Id, Guid NodeId, string Number, Guid DeviceId, Guid CashierId, string Status,
        DateTime OpenedAt, decimal OpeningCash, int RowVersion);
}
