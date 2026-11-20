using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class SaleCalculatorTests
{
    private static readonly CashRoundingSettings Round100 = new(100m, CashRoundingMode.Nearest);
    private static readonly CashRoundingSettings NoRounding = new(0m, CashRoundingMode.Nearest);

    private static PaymentInput Cash(string amount) => new(PaymentKind.Cash, D(amount));
    private static PaymentInput Qris(string amount) => new(PaymentKind.NonCash, D(amount));
    private static PaymentInput Credit(string amount) => new(PaymentKind.Credit, D(amount));

    private static SaleTotals Calc(decimal[] lineTotals, PaymentInput[] payments,
        CashRoundingSettings? rounding = null, bool hasCustomer = false) =>
        SaleCalculator.Calculate(lineTotals, payments, rounding ?? Round100, hasCustomer);

    [Fact]
    public void Cash_sale_rounds_total_and_gives_change()
    {
        // 12340 + 5000 = 17340 -> 17300 (nearest 100); paid 20000 -> change 2700
        var t = Calc([12340m, 5000m], [Cash("20000")]);

        Assert.Equal(17340m, t.Subtotal);
        Assert.Equal(-40m, t.RoundingAmount);
        Assert.Equal(17300m, t.Total);
        Assert.Equal(2700m, t.Change);
        Assert.Equal(0m, t.CreditAmount);

        var cash = Assert.Single(t.Payments);
        Assert.Equal(17300m, cash.Applied);
        Assert.Equal(20000m, cash.Tendered);
        Assert.Equal(2700m, cash.ChangeGiven);
    }

    [Fact]
    public void Exact_cash_has_no_change()
    {
        var t = Calc([17300m], [Cash("17300")]);

        Assert.Equal(0m, t.Change);
        Assert.Equal(0m, Assert.Single(t.Payments).ChangeGiven);
    }

    [Fact]
    public void Non_cash_only_sale_is_not_rounded()
    {
        var t = Calc([17340m], [Qris("17340")]);

        Assert.Equal(0m, t.RoundingAmount);
        Assert.Equal(17340m, t.Total);
        var qris = Assert.Single(t.Payments);
        Assert.Equal(17340m, qris.Applied);
        Assert.Null(qris.Tendered);
        Assert.Null(qris.ChangeGiven);
    }

    [Fact]
    public void Mixed_qris_and_cash_change_comes_from_cash()
    {
        // total 17300 (rounded because cash is used); QRIS 10000, cash 10000 -> change 2700
        var t = Calc([17340m], [Qris("10000"), Cash("10000")]);

        Assert.Equal(17300m, t.Total);
        Assert.Equal(2700m, t.Change);
        Assert.Equal(10000m, t.Payments[0].Applied);
        Assert.Equal(7300m, t.Payments[1].Applied);
        Assert.Equal(10000m, t.Payments[1].Tendered);
        Assert.Equal(2700m, t.Payments[1].ChangeGiven);
        Assert.Equal(t.Total, t.Payments.Sum(p => p.Applied));
    }

    [Fact]
    public void Insufficient_payment_is_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([17340m], [Cash("17000")]));
        Assert.Equal("insufficient_payment", ex.Code);
    }

    [Fact]
    public void Non_cash_overpayment_is_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([17340m], [Qris("20000")]));
        Assert.Equal("non_cash_overpayment", ex.Code);
    }

    [Fact]
    public void Non_cash_overpayment_with_cash_is_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([10000m], [Qris("12000"), Cash("5000")]));
        Assert.Equal("non_cash_overpayment", ex.Code);
    }

    [Fact]
    public void Credit_sale_needs_a_customer()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([50000m], [Credit("50000")]));
        Assert.Equal("credit_requires_customer", ex.Code);
    }

    [Fact]
    public void Partial_credit_with_cash()
    {
        // total 50000: cash 20000, credit 30000
        var t = Calc([50000m], [Cash("20000"), Credit("30000")], hasCustomer: true);

        Assert.Equal(30000m, t.CreditAmount);
        Assert.Equal(0m, t.Change);
        Assert.Equal(50000m, t.Payments.Sum(p => p.Applied));
    }

    [Fact]
    public void Full_credit_sale_is_not_rounded()
    {
        var t = Calc([50050m], [Credit("50050")], hasCustomer: true);

        Assert.Equal(50050m, t.Total);
        Assert.Equal(50050m, t.CreditAmount);
    }

    [Fact]
    public void Credit_cannot_exceed_total()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([50000m], [Credit("60000")], hasCustomer: true));
        Assert.Equal("non_cash_overpayment", ex.Code);
    }

    [Fact]
    public void Only_one_cash_payment()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([10000m], [Cash("5000"), Cash("5000")]));
        Assert.Equal("multiple_cash_payments", ex.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-100")]
    public void Payment_amount_must_be_positive(string amount)
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([10000m], [Cash(amount)]));
        Assert.Equal("invalid_payment_amount", ex.Code);
    }

    [Fact]
    public void Sale_needs_lines()
    {
        var ex = Assert.Throws<SaleValidationException>(() => Calc([], [Cash("1000")]));
        Assert.Equal("no_lines", ex.Code);
    }

    [Fact]
    public void Zero_total_sale_needs_no_payment()
    {
        // e.g. a free item: 100% discount
        var t = Calc([0m], []);

        Assert.Equal(0m, t.Total);
        Assert.Empty(t.Payments);
    }

    [Fact]
    public void Rounding_to_zero_with_cash()
    {
        // 40 rounds to 0 with a 100 step; any cash handed over is change
        var t = Calc([40m], [Cash("100")]);

        Assert.Equal(0m, t.Total);
        Assert.Equal(100m, t.Change);
    }

    [Fact]
    public void Rounding_disabled_keeps_exact_total()
    {
        var t = Calc([17340m], [Cash("20000")], NoRounding);

        Assert.Equal(17340m, t.Total);
        Assert.Equal(2660m, t.Change);
    }

    [Fact]
    public void Rounding_up_mode()
    {
        var t = Calc([17301m], [Cash("20000")], new CashRoundingSettings(100m, CashRoundingMode.Up));

        Assert.Equal(17400m, t.Total);
        Assert.Equal(99m, t.RoundingAmount);
    }

    [Fact]
    public void Invariants_hold()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ => (decimal)random.Next(0, 100000)).ToArray();
            var subtotal = lines.Sum();
            var qris = random.Next(2) == 0 ? Math.Min(subtotal, random.Next(0, 50000)) : 0m;
            var cash = subtotal - qris + random.Next(0, 20000) + 100m;
            PaymentInput[] payments = qris > 0 ? [Qris(qris.ToString(System.Globalization.CultureInfo.InvariantCulture)), new(PaymentKind.Cash, cash)] : [new(PaymentKind.Cash, cash)];

            SaleTotals t;
            try { t = Calc(lines, payments); }
            catch (SaleValidationException) { continue; }   // e.g. qris above the rounded-down total

            Assert.Equal(t.Subtotal + t.RoundingAmount, t.Total);
            Assert.Equal(t.Total, t.Payments.Sum(p => p.Applied));
            Assert.Equal(payments.Sum(p => p.Amount) - t.Total, t.Change);
            Assert.True(t.Change >= 0);
        }
    }
}
