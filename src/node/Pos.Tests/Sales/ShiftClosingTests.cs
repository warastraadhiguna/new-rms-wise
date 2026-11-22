using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class ShiftClosingTests
{
    private static ShiftTotals Totals(
        decimal opening = 200000m, decimal cashSales = 0m, decimal downPayments = 0m, decimal receivableCash = 0m,
        decimal expenses = 0m) =>
        new(opening, cashSales, downPayments, receivableCash, expenses, [], 0, 0);

    [Fact]
    public void Expected_cash_is_opening_plus_cash_in_minus_expenses()
    {
        // opening 200.000, cash sales 137.500, DP 50.000, receivable paid in cash 25.000, expense 12.000
        var totals = Totals(200000m, 137500m, 50000m, 25000m, 12000m);

        Assert.Equal(400500m, ShiftClosing.ExpectedCash(totals));
    }

    [Fact]
    public void Without_transactions_the_expected_cash_is_the_opening_cash()
    {
        Assert.Equal(150000m, ShiftClosing.ExpectedCash(Totals(opening: 150000m)));
    }

    [Fact]
    public void Zero_opening_cash_is_allowed()
    {
        Assert.Equal(10000m, ShiftClosing.ExpectedCash(Totals(opening: 0m, cashSales: 10000m)));
    }

    [Fact]
    public void Expenses_above_the_cash_give_a_negative_expected_cash()
    {
        // a fact of the shift (e.g. an expense paid from the owner's pocket recorded on it); the difference shows it
        Assert.Equal(-5000m, ShiftClosing.ExpectedCash(Totals(opening: 0m, cashSales: 10000m, expenses: 15000m)));
    }

    [Fact]
    public void Non_cash_payments_never_count_toward_the_drawer()
    {
        var totals = Totals(opening: 100000m, cashSales: 20000m) with
        {
            NonCash = [new ShiftMethodTotal("QRIS", "QRIS", 75000m, 3), new ShiftMethodTotal("CREDIT", "KREDIT", 400000m, 1)],
        };

        Assert.Equal(120000m, ShiftClosing.ExpectedCash(totals));
    }

    [Fact]
    public void Decimal_amounts_are_kept_exactly()
    {
        Assert.Equal(100000.0003m, ShiftClosing.ExpectedCash(Totals(opening: 99999.9999m, cashSales: 0.0004m)));
    }

    [Theory]
    [InlineData("-1", "0", "0", "0", "0")]
    [InlineData("0", "-1", "0", "0", "0")]
    [InlineData("0", "0", "-1", "0", "0")]
    [InlineData("0", "0", "0", "-1", "0")]
    [InlineData("0", "0", "0", "0", "-1")]
    public void Negative_totals_are_rejected(string opening, string cash, string dp, string receivable, string expenses)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShiftClosing.ExpectedCash(Totals(D(opening), D(cash), D(dp), D(receivable), D(expenses))));
    }

    [Theory]
    [InlineData("337500", "337500", "0", ShiftDifferenceKind.Exact, "Pas")]
    [InlineData("337000", "337500", "-500", ShiftDifferenceKind.Short, "Kurang")]
    [InlineData("338000", "337500", "500", ShiftDifferenceKind.Over, "Lebih")]
    [InlineData("0", "-5000", "5000", ShiftDifferenceKind.Over, "Lebih")]
    public void Difference_is_counted_minus_expected(string counted, string expected, string difference, ShiftDifferenceKind kind, string label)
    {
        var diff = ShiftClosing.Difference(D(counted), D(expected));

        Assert.Equal(D(difference), diff);
        Assert.Equal(kind, ShiftClosing.Kind(diff));
        Assert.Equal(label, ShiftClosing.Label(diff));
    }

    [Fact]
    public void Negative_counted_cash_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShiftClosing.Difference(-1m, 0m));
    }

    [Fact]
    public void Negative_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShiftClosing.ExpectedCash(Totals() with { SaleCount = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShiftClosing.ExpectedCash(Totals() with { CancellationCount = -1 }));
    }

    private static decimal D(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

public class ShiftClosingRefundTests
{
    [Fact]
    public void Cash_refunds_of_returns_reduce_the_expected_cash()
    {
        var totals = new ShiftTotals(200000m, 50000m, 0m, 0m, 0m, [], 3, 0, CashRefunds: 12000m);

        Assert.Equal(238000m, ShiftClosing.ExpectedCash(totals));
    }

    [Fact]
    public void Negative_refunds_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShiftClosing.ExpectedCash(new ShiftTotals(0m, 0m, 0m, 0m, 0m, [], 0, 0, CashRefunds: -1m)));
    }
}
