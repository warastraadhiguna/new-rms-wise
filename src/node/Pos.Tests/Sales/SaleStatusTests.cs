using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class SaleStatusTests
{
    [Theory]
    [InlineData("0", "0", "0", false, "0")]               // cash sale
    [InlineData("45000", "0", "0", false, "45000")]       // credit, nothing paid
    [InlineData("45000", "20000", "0", false, "25000")]   // partly paid
    [InlineData("45000", "20000", "10000", false, "15000")] // partly paid, a return reduced the receivable
    [InlineData("45000", "50000", "0", false, "0")]       // overpaid never gives a negative balance
    [InlineData("45000", "0", "0", true, "0")]            // voided: the receivable is cancelled
    public void Outstanding_is_credit_minus_payments_and_receivable_returns(
        string credit, string paid, string reduced, bool voided, string expected)
    {
        Assert.Equal(D(expected), SaleBalance.Outstanding(D(credit), D(paid), D(reduced), voided));
    }

    [Fact]
    public void Negative_amounts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleBalance.Outstanding(-1m, 0m, 0m, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleBalance.Outstanding(0m, -1m, 0m, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleBalance.Outstanding(0m, 0m, -1m, false));
    }

    [Theory]
    [InlineData(false, "0", false, SaleStatuses.Paid, "Lunas")]
    [InlineData(false, "25000", false, SaleStatuses.Unpaid, "Belum Lunas")]
    [InlineData(false, "0", true, SaleStatuses.Returned, "Ada Retur")]
    [InlineData(false, "25000", true, SaleStatuses.Unpaid, "Belum Lunas")]   // unpaid money matters more than a return
    [InlineData(true, "0", true, SaleStatuses.Void, "Void")]
    [InlineData(true, "0", false, SaleStatuses.Void, "Void")]
    public void Status_void_then_unpaid_then_returned_then_paid(bool voided, string outstanding, bool hasReturn, string status, string label)
    {
        var code = SaleBalance.Status(voided, D(outstanding), hasReturn);

        Assert.Equal(status, code);
        Assert.Equal(label, SaleStatuses.Label(code));
    }

    [Fact]
    public void Unknown_status_code_is_shown_as_is()
    {
        Assert.Equal("X", SaleStatuses.Label("X"));
    }

    private static decimal D(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
