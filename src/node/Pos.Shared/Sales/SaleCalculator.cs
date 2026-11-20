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

/// <summary>
/// Sale (header) discount: a percentage OR a nominal amount, applied after the line totals.
/// </summary>
public readonly record struct HeaderDiscount(decimal? PercentValue, decimal? AmountValue)
{
    public static HeaderDiscount None => new(null, null);

    public static HeaderDiscount Percent(decimal percent) => new(percent, null);

    public static HeaderDiscount Nominal(decimal amount) => new(null, amount);

    /// <summary>From request fields; zero counts as not given; both given is rejected.</summary>
    public static HeaderDiscount From(decimal? percent, decimal? amount)
    {
        var hasPercent = percent is { } p && p != 0;
        var hasAmount = amount is { } a && a != 0;
        if (hasPercent && hasAmount)
            throw new SaleValidationException("invalid_discount", "Give the sale discount as a percentage or as an amount, not both.");
        return hasPercent ? Percent(percent!.Value) : hasAmount ? Nominal(amount!.Value) : None;
    }
}

/// <summary>Header totals and payments of a sale. <c>Total = Subtotal - DiscountAmount + RoundingAmount</c> (tax: TODO).</summary>
public sealed record SaleTotals(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal RoundingAmount,
    decimal Total,
    decimal CreditAmount,
    decimal Change,
    IReadOnlyList<PaymentAllocation> Payments);

/// <summary>
/// Sale header totals and payment validation. Cash rounding applies only when cash is used and nothing is on credit.
/// Rules: payments >= total; non-cash and credit together never exceed the total (change only from cash);
/// at most one cash payment; credit needs a customer.
/// </summary>
public static class SaleCalculator
{
    public static SaleTotals Calculate(
        IReadOnlyList<decimal> lineTotals,
        IReadOnlyList<PaymentInput> payments,
        CashRoundingSettings rounding,
        bool hasCustomer,
        HeaderDiscount discount = default,
        int currencyDecimals = Rupiah.DefaultCurrencyDecimals)
    {
        ArgumentNullException.ThrowIfNull(lineTotals);
        ArgumentNullException.ThrowIfNull(payments);

        if (lineTotals.Count == 0)
            throw new SaleValidationException("no_lines", "A sale needs at least one line.");
        if (payments.Any(p => p.Amount <= 0))
            throw new SaleValidationException("invalid_payment_amount", "Every payment amount must be greater than zero.");
        if (payments.Count(p => p.Kind == PaymentKind.Cash) > 1)
            throw new SaleValidationException("multiple_cash_payments", "Use one cash payment per sale.");

        // Owner decision: cash rounding only for sales paid in cash without any credit; KREDIT (with or without a
        // cash DP) is never rounded.
        var rounds = payments.Any(p => p.Kind == PaymentKind.Cash) && payments.All(p => p.Kind != PaymentKind.Credit);
        var subtotal = lineTotals.Sum();
        var discountAmount = HeaderDiscountAmount(discount, subtotal, currencyDecimals);
        var afterDiscount = subtotal - discountAmount;
        var (total, roundingAmount) = rounds
            ? Rupiah.RoundCash(afterDiscount, rounding.Step, rounding.Mode)
            : new CashRoundingResult(afterDiscount, 0m);

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

        return new SaleTotals(subtotal, discountAmount, roundingAmount, total, credit, change, allocations);
    }

    /// <summary>Rounded to the currency precision and never more than the subtotal.</summary>
    public static decimal HeaderDiscountAmount(HeaderDiscount discount, decimal subtotal, int currencyDecimals)
    {
        if (discount.PercentValue is { } pct)
        {
            if (pct is < 0 or > 100)
                throw new SaleValidationException("invalid_discount", "The sale discount percentage must be between 0 and 100.");
            return Math.Min(subtotal, Rupiah.RoundToCurrency(subtotal * pct / 100m, currencyDecimals));
        }

        if (discount.AmountValue is { } amount)
        {
            if (amount < 0)
                throw new SaleValidationException("invalid_discount", "The sale discount must not be negative.");
            return Math.Min(subtotal, Rupiah.RoundToCurrency(amount, currencyDecimals));
        }

        return 0m;
    }
}

/// <summary>
/// How much sale discount a user may give without a supervisor. Permission <c>discount.max:N</c> allows up to
/// N percent (the largest N wins); <c>*</c> has no limit; without such a permission the limit is 0%.
/// </summary>
public static class DiscountPolicy
{
    public const string MaxPrefix = "discount.max:";

    /// <returns>The maximum percentage, or null for no limit.</returns>
    public static decimal? MaxPercent(Auth.PermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Names.Contains(Auth.Permissions.All))
            return null;

        return permissions.Names
            .Where(n => n.StartsWith(MaxPrefix, StringComparison.Ordinal))
            .Select(n => decimal.TryParse(n[MaxPrefix.Length..], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null)
            .Where(v => v is >= 0)
            .Select(v => v!.Value)
            .DefaultIfEmpty(0m)
            .Max();
    }

    /// <summary>
    /// True when the discount is above what <paramref name="maxPercent"/> allows. The allowed amount is rounded
    /// like the discount itself, so "10%" rounded up never needs approval.
    /// </summary>
    public static bool RequiresApproval(decimal discountAmount, decimal subtotal, decimal? maxPercent, int currencyDecimals)
    {
        if (discountAmount <= 0 || maxPercent is null)
            return false;
        var allowed = Rupiah.RoundToCurrency(subtotal * maxPercent.Value / 100m, currencyDecimals);
        return discountAmount > allowed;
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
