using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class CreditCheckTests
{
    [Fact]
    public void Within_limit_has_no_warning()
    {
        var r = CreditCheck.Evaluate(creditLimit: 500000m, outstanding: 100000m, newCredit: 400000m);

        Assert.False(r.LimitExceeded);
        Assert.Equal(500000m, r.OutstandingAfter);
    }

    [Fact]
    public void Over_limit_is_allowed_with_warning()
    {
        var r = CreditCheck.Evaluate(500000m, 450000m, 100000m);

        Assert.True(r.LimitExceeded);
        Assert.Equal(550000m, r.OutstandingAfter);
        Assert.Equal(50000m, r.ExceededBy);
    }

    [Fact]
    public void Customer_without_credit_facility_is_rejected()
    {
        var ex = Assert.Throws<SaleValidationException>(() => CreditCheck.Evaluate(null, 0m, 1000m));
        Assert.Equal("credit_not_allowed", ex.Code);
    }

    [Fact]
    public void Zero_limit_means_every_credit_is_over_the_limit()
    {
        Assert.True(CreditCheck.Evaluate(0m, 0m, 1m).LimitExceeded);
    }

    [Fact]
    public void Rejects_non_positive_new_credit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreditCheck.Evaluate(1000m, 0m, 0m));
    }
}
