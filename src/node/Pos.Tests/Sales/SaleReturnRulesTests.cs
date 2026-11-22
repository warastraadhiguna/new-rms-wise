using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class SaleReturnRulesTests
{
    // ---- price: net per unit after line discount and the sale discount share ----------------------

    [Fact]
    public void Net_line_total_takes_the_sale_discount_share()
    {
        // line 3 x 10.000 = 30.000; sale subtotal 60.000 with Potongan 6.000 (10%) -> line net 27.000
        Assert.Equal(27000m, ReturnPricing.NetLineTotal(lineTotal: 30000m, saleSubtotal: 60000m, saleDiscount: 6000m));
    }

    [Fact]
    public void Without_a_sale_discount_the_net_is_the_line_total()
    {
        Assert.Equal(30000m, ReturnPricing.NetLineTotal(30000m, 60000m, 0m));
        Assert.Equal(0m, ReturnPricing.NetLineTotal(0m, 0m, 0m));                     // free item
    }

    [Fact]
    public void Net_unit_price_keeps_four_decimals()
    {
        Assert.Equal(3333.3333m, ReturnPricing.NetUnitPrice(netLineTotal: 10000m, soldQty: 3m));
    }

    [Fact]
    public void Partial_returns_add_up_exactly_to_the_net_line_total()
    {
        // net 10.000 for 3 units; returned one at a time
        var first = ReturnPricing.RefundAmount(10000m, soldQty: 3m, returnedBefore: 0m, returnQty: 1m, currencyDecimals: 0);
        var second = ReturnPricing.RefundAmount(10000m, 3m, 1m, 1m, 0);
        var third = ReturnPricing.RefundAmount(10000m, 3m, 2m, 1m, 0);

        Assert.Equal((3333m, 3334m, 3333m), (first, second, third));
        Assert.Equal(10000m, first + second + third);
    }

    [Fact]
    public void Decimal_quantities()
    {
        Assert.Equal(12500m, ReturnPricing.RefundAmount(25000m, soldQty: 2.5m, returnedBefore: 0m, returnQty: 1.25m, currencyDecimals: 0));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2.0001")]                 // 3 sold, 1 already returned
    public void Return_quantity_must_be_positive_and_within_what_remains(string qty)
    {
        Assert.Throws<SaleValidationException>(() =>
            ReturnPricing.RefundAmount(10000m, 3m, 1m, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), 0));
    }

    [Fact]
    public void Returnable_is_sold_minus_returned_never_negative()
    {
        Assert.Equal(2m, ReturnPricing.Returnable(3m, 1m));
        Assert.Equal(0m, ReturnPricing.Returnable(3m, 5m));
    }

    // ---- window ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 7, true)]
    [InlineData(7, 7, true)]       // the 7th day still counts
    [InlineData(8, 7, false)]
    [InlineData(1, 0, false)]      // window 0 = same day only
    [InlineData(0, 0, true)]
    public void Window_counts_branch_days_since_the_sale(int daysAgo, int windowDays, bool within)
    {
        var today = new DateOnly(2026, 11, 22);

        Assert.Equal(within, ReturnPricing.WithinWindow(today.AddDays(-daysAgo), today, windowDays));
    }

    // ---- settlement ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReturnSettlements.Refund, "Uang Kembali")]
    [InlineData(ReturnSettlements.Exchange, "Tukar Barang")]
    [InlineData(ReturnSettlements.ReduceReceivable, "Potong Piutang")]
    [InlineData("X", "X")]
    public void Settlement_labels(string code, string label)
    {
        Assert.Equal(label, ReturnSettlements.Label(code));
    }

    [Fact]
    public void Potong_piutang_only_for_a_credit_nota_and_up_to_what_is_owed()
    {
        ReturnSettlements.Validate(ReturnSettlements.ReduceReceivable, total: 20000m, saleCreditAmount: 45000m, outstanding: 20000m);
        ReturnSettlements.Validate(ReturnSettlements.Refund, 20000m, 0m, 0m);
        ReturnSettlements.Validate(ReturnSettlements.Exchange, 20000m, 0m, 0m);

        Assert.Equal("not_a_credit_sale", Assert.Throws<SaleValidationException>(() =>
            ReturnSettlements.Validate(ReturnSettlements.ReduceReceivable, 20000m, 0m, 0m)).Code);
        Assert.Equal("exceeds_receivable", Assert.Throws<SaleValidationException>(() =>
            ReturnSettlements.Validate(ReturnSettlements.ReduceReceivable, 20001m, 45000m, 20000m)).Code);
        Assert.Equal("invalid_settlement", Assert.Throws<SaleValidationException>(() =>
            ReturnSettlements.Validate("CASHBACK", 1m, 0m, 0m)).Code);
    }
}
