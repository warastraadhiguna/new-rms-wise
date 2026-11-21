using Pos.Shared.Stock;

namespace Pos.Tests.Stock;

public class ExpiryStatusTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Fact]
    public void No_expiry_date_has_no_marker()
    {
        Assert.Equal(ExpiryState.NoExpiry, ExpiryStatus.Evaluate(null, Today, 30));
        Assert.Null(ExpiryStatus.Label(ExpiryState.NoExpiry));
    }

    [Theory]
    [InlineData(-1, ExpiryState.Expired)]      // yesterday
    [InlineData(-400, ExpiryState.Expired)]
    [InlineData(0, ExpiryState.NearExpiry)]    // expires today: still sellable (same rule as FEFO), but near
    [InlineData(1, ExpiryState.NearExpiry)]
    [InlineData(30, ExpiryState.NearExpiry)]   // exactly the warning limit
    [InlineData(31, ExpiryState.Ok)]
    [InlineData(365, ExpiryState.Ok)]
    public void Expired_before_today_near_within_the_warning_days(int daysFromToday, ExpiryState expected)
    {
        Assert.Equal(expected, ExpiryStatus.Evaluate(Today.AddDays(daysFromToday), Today, 30));
    }

    [Fact]
    public void Zero_warning_days_marks_only_today_as_near()
    {
        Assert.Equal(ExpiryState.NearExpiry, ExpiryStatus.Evaluate(Today, Today, 0));
        Assert.Equal(ExpiryState.Ok, ExpiryStatus.Evaluate(Today.AddDays(1), Today, 0));
    }

    [Fact]
    public void Negative_warning_days_count_as_zero()
    {
        Assert.Equal(ExpiryState.Ok, ExpiryStatus.Evaluate(Today.AddDays(1), Today, -5));
        Assert.Equal(ExpiryState.Expired, ExpiryStatus.Evaluate(Today.AddDays(-1), Today, -5));
    }

    [Fact]
    public void Labels_are_the_rms2_texts()
    {
        Assert.Equal("(HAMPIR EXPIRED)", ExpiryStatus.Label(ExpiryState.NearExpiry));
        Assert.Equal("(SUDAH EXPIRED)", ExpiryStatus.Label(ExpiryState.Expired));
        Assert.Null(ExpiryStatus.Label(ExpiryState.Ok));
    }

    [Theory]
    [InlineData("expired", ExpiryState.Expired)]
    [InlineData("near_expiry", ExpiryState.NearExpiry)]
    [InlineData("ok", ExpiryState.Ok)]
    [InlineData("no_expiry", ExpiryState.NoExpiry)]
    public void Codes_round_trip(string code, ExpiryState state)
    {
        Assert.Equal(code, ExpiryStatus.ToCode(state));
        Assert.Equal(state, ExpiryStatus.FromCode(code));
    }
}
