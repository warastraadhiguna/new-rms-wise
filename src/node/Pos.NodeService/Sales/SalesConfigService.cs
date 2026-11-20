using Dapper;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Auth;
using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.NodeService.Sales;

public sealed record PaymentMethodDto(Guid Id, string Code, string Name, bool IsCash, bool IsCredit);

/// <param name="DiscountMaxPercent">The user's sale-discount limit without a supervisor; null = no limit.</param>
public sealed record SalesConfigDto(
    IReadOnlyList<PaymentMethodDto> PaymentMethods,
    decimal CashRoundingStep,
    string CashRoundingMode,
    int CurrencyDecimals,
    decimal? DiscountMaxPercent);

/// <summary>
/// What the payment dialog needs to show the same Total and Kembali as the server will calculate:
/// active payment methods (RMS 2 order: TUNAI, DEBIT, QRIS, TRANSFER, KREDIT, then others by name),
/// cash rounding, currency precision and the user's discount limit.
/// </summary>
public sealed class SalesConfigService(DbConnectionFactory connections, NodeIdentity nodeIdentity)
{
    public async Task<SalesConfigDto> GetAsync(PermissionSet permissions, CancellationToken ct = default)
    {
        var node = await nodeIdentity.GetAsync(ct);
        await using var c = await connections.OpenAsync(ct);

        var methods = (await c.QueryAsync<PaymentMethodDto>(new CommandDefinition(
            """
            SELECT id AS Id, code AS Code, name AS Name, is_cash AS IsCash, is_credit AS IsCredit
            FROM payment_methods WHERE is_active = 1 AND deleted_at IS NULL
            ORDER BY FIELD(code, 'CASH', 'DEBIT', 'QRIS', 'TRANSFER', 'CREDIT') = 0,
                     FIELD(code, 'CASH', 'DEBIT', 'QRIS', 'TRANSFER', 'CREDIT'), name
            """, cancellationToken: ct))).ToList();

        await using var tx = await c.BeginTransactionAsync(ct);
        var settings = await SaleSettings.LoadAsync(c, tx, node.BranchId, ct);
        await tx.CommitAsync(ct);

        return new SalesConfigDto(methods, settings.Rounding.Step, settings.Rounding.Mode.ToCode(), settings.CurrencyDecimals,
            DiscountPolicy.MaxPercent(permissions));
    }
}
