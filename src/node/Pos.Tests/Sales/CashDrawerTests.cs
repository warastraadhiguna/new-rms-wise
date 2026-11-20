using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class CashDrawerTests
{
    [Fact]
    public void Expected_cash_is_opening_plus_cash_received_minus_change()
    {
        // opening 200000, customers handed over 150000 in cash, 12500 given back as change
        Assert.Equal(337500m, CashDrawer.ExpectedCash(openingCash: 200000m, cashTendered: 150000m, changeGiven: 12500m));
    }

    [Fact]
    public void No_sales_expected_cash_is_the_opening_cash()
    {
        Assert.Equal(100000m, CashDrawer.ExpectedCash(100000m, 0m, 0m));
    }

    [Fact]
    public void Zero_opening_cash_is_allowed()
    {
        Assert.Equal(5000m, CashDrawer.ExpectedCash(0m, 10000m, 5000m));
    }

    [Theory]
    [InlineData("-1", "0", "0")]
    [InlineData("0", "-1", "0")]
    [InlineData("0", "0", "-1")]
    public void Negative_amounts_are_rejected(string opening, string tendered, string change)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CashDrawer.ExpectedCash(D(opening), D(tendered), D(change)));
    }

    [Fact]
    public void Change_cannot_exceed_cash_received_plus_opening()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CashDrawer.ExpectedCash(1000m, 5000m, 7000m));
    }

    [Theory]
    [InlineData("337500", "337500", "0")]
    [InlineData("337000", "337500", "-500")]    // short
    [InlineData("338000", "337500", "500")]     // over
    public void Difference_is_counted_minus_expected(string counted, string expected, string difference)
    {
        Assert.Equal(D(difference), CashDrawer.Difference(D(counted), D(expected)));
    }
}
