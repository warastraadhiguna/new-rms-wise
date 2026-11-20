using Pos.Shared.Money;

namespace Pos.Shared.Sales;

/// <summary>A sale was rejected by a business rule. <c>Code</c> is stable and machine-readable.</summary>
public sealed class SaleValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum PaymentKind
{
    /// <summary>Counts toward the cash drawer; the only kind that can give change.</summary>
    Cash,

    /// <summary>QRIS, transfer, debit: exact amount, no change.</summary>
    NonCash,

    /// <summary>Unpaid part that becomes a receivable of the customer.</summary>
    Credit,
}

/// <param name="Amount">Money handed over with this method (for cash: what the customer gives).</param>
public readonly record struct PaymentInput(PaymentKind Kind, decimal Amount);

/// <param name="Applied">Part of the sale total settled by this payment (<c>sale_payments.amount</c>).</param>
/// <param name="Tendered">Cash handed over; null for non-cash.</param>
/// <param name="ChangeGiven">Change returned; null for non-cash.</param>
public readonly record struct PaymentAllocation(PaymentKind Kind, decimal Applied, decimal? Tendered, decimal? ChangeGiven);

public readonly record struct CashRoundingSettings(decimal Step, CashRoundingMode Mode);

/// <summary>Header totals and payments of a sale. <c>Total = Subtotal + RoundingAmount</c> (tax and header discount: TODO).</summary>
public sealed record SaleTotals(
    decimal Subtotal,
    decimal RoundingAmount,
    decimal Total,
    decimal CreditAmount,
    decimal Change,
    IReadOnlyList<PaymentAllocation> Payments);

/// <summary>
/// Sale header totals and payment validation. Cash rounding applies only when cash is used.
/// Rules: payments >= total; non-cash and credit together never exceed the total (change only from cash);
/// at most one cash payment; credit needs a customer.
/// </summary>
public static class SaleCalculator
{
    public static SaleTotals Calculate(
        IReadOnlyList<decimal> lineTotals,
        IReadOnlyList<PaymentInput> payments,
        CashRoundingSettings rounding,
        bool hasCustomer)
    {
        ArgumentNullException.ThrowIfNull(lineTotals);
        ArgumentNullException.ThrowIfNull(payments);

        if (lineTotals.Count == 0)
            throw new SaleValidationException("no_lines", "A sale needs at least one line.");
        if (payments.Any(p => p.Amount <= 0))
            throw new SaleValidationException("invalid_payment_amount", "Every payment amount must be greater than zero.");
        if (payments.Count(p => p.Kind == PaymentKind.Cash) > 1)
            throw new SaleValidationException("multiple_cash_payments", "Use one cash payment per sale.");

        var hasCash = payments.Any(p => p.Kind == PaymentKind.Cash);
        var subtotal = lineTotals.Sum();
        var (total, roundingAmount) = hasCash
            ? Rupiah.RoundCash(subtotal, rounding.Step, rounding.Mode)
            : new CashRoundingResult(subtotal, 0m);

        var nonCash = payments.Where(p => p.Kind != PaymentKind.Cash).Sum(p => p.Amount);
        if (nonCash > total)
            throw new SaleValidationException("non_cash_overpayment",
                $"Non-cash and credit payments ({nonCash}) exceed the total ({total}); change can only be given from cash.");

        var paid = payments.Sum(p => p.Amount);
        if (paid < total)
            throw new SaleValidationException("insufficient_payment", $"Payments ({paid}) are less than the total ({total}).");

        var credit = payments.Where(p => p.Kind == PaymentKind.Credit).Sum(p => p.Amount);
        if (credit > 0 && !hasCustomer)
            throw new SaleValidationException("credit_requires_customer", "A credit payment needs a customer.");

        var change = paid - total;   // <= cash amount, because non-cash <= total
        var allocations = payments.Select(p => p.Kind == PaymentKind.Cash
                ? new PaymentAllocation(p.Kind, p.Amount - change, p.Amount, change)
                : new PaymentAllocation(p.Kind, p.Amount, null, null))
            .ToList();

        return new SaleTotals(subtotal, roundingAmount, total, credit, change, allocations);
    }
}

/// <param name="ExceededBy">How far the outstanding balance goes over the limit (0 when within).</param>
public readonly record struct CreditCheckResult(bool LimitExceeded, decimal OutstandingAfter, decimal ExceededBy);

/// <summary>
/// Credit limit check against local data. Exceeding the limit is allowed offline but flagged;
/// a customer without a limit (<c>credit_limit</c> NULL) has no credit facility.
/// </summary>
public static class CreditCheck
{
    public static CreditCheckResult Evaluate(decimal? creditLimit, decimal outstanding, decimal newCredit)
    {
        if (newCredit <= 0)
            throw new ArgumentOutOfRangeException(nameof(newCredit), newCredit, "New credit must be greater than zero.");
        if (creditLimit is null)
            throw new SaleValidationException("credit_not_allowed", "This customer has no credit facility.");

        var after = outstanding + newCredit;
        var exceededBy = Math.Max(0m, after - creditLimit.Value);
        return new CreditCheckResult(exceededBy > 0, after, exceededBy);
    }
}
