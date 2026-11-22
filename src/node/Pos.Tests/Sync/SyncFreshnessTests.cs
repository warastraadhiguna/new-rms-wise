using Pos.Shared.Sync;

namespace Pos.Tests.Sync;

public class SyncFreshnessTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "baru saja")]
    [InlineData(59, "baru saja")]
    [InlineData(60, "1 menit lalu")]
    [InlineData(120, "2 menit lalu")]
    [InlineData(59 * 60 + 59, "59 menit lalu")]
    [InlineData(3600, "1 jam lalu")]
    [InlineData(23 * 3600, "23 jam lalu")]
    [InlineData(2 * 86400, "2 hari lalu")]
    public void Says_how_long_ago_the_last_success_was(int secondsAgo, string expected)
    {
        var text = SyncFreshness.Describe(true, Now.AddSeconds(-secondsAgo), Now, 0, 0, 15);
        Assert.Equal($"Sinkron: {expected} · 0 tertunda", text.Text);
    }

    [Fact]
    public void Is_red_after_the_alert_minutes_only()
    {
        Assert.False(SyncFreshness.Describe(true, Now.AddMinutes(-15), Now, 3, 0, 15).IsAlert);
        Assert.True(SyncFreshness.Describe(true, Now.AddMinutes(-16), Now, 3, 0, 15).IsAlert);
        Assert.False(SyncFreshness.Describe(true, Now.AddMinutes(-40), Now, 0, 0, 45).IsAlert);
    }

    [Fact]
    public void Never_synced_is_red_and_off_is_not()
    {
        Assert.Equal(new SyncFreshnessText("Sinkron: belum pernah · 4 tertunda", true), SyncFreshness.Describe(true, null, Now, 4, 0, 15));
        Assert.Equal(new SyncFreshnessText("Sinkron: tidak aktif", false), SyncFreshness.Describe(false, null, Now, 4, 0, 15));
    }

    [Fact]
    public void Refused_documents_show_and_make_it_red()
    {
        var text = SyncFreshness.Describe(true, Now.AddSeconds(-10), Now, 1, 2, 15);
        Assert.Equal("Sinkron: baru saja · 1 tertunda · 2 gagal", text.Text);
        Assert.True(text.IsAlert);
    }

    [Fact]
    public void Odd_values_never_break_the_text()
    {
        Assert.Equal("Sinkron: baru saja · 0 tertunda", SyncFreshness.Describe(true, Now.AddSeconds(5), Now, -3, 0, 0).Text);   // clock ahead
        Assert.True(SyncFreshness.Describe(true, Now.AddMinutes(-2), Now, 0, 0, 0).IsAlert);                                  // limit at least 1 minute
    }
}
