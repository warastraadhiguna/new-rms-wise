using Pos.Shared.Time;

namespace Pos.Tests.Time;

public class BranchClockTests
{
    [Theory]
    [InlineData("2026-11-20T16:59:59Z", "2026-11-20")]   // 23:59:59 WIB
    [InlineData("2026-11-20T17:00:00Z", "2026-11-21")]   // 00:00 WIB next day
    [InlineData("2026-11-20T00:00:00Z", "2026-11-20")]
    public void Today_uses_branch_timezone(string utcNow, string expected)
    {
        Assert.Equal(Day(expected), BranchClock.Today(Utc(utcNow), "Asia/Jakarta"));
    }

    [Fact]
    public void Today_for_eastern_indonesia()
    {
        // WIT = UTC+9: 15:00Z is already 00:00 next day
        Assert.Equal(Day("2026-11-21"), BranchClock.Today(Utc("2026-11-20T15:00:00Z"), "Asia/Jayapura"));
    }

    [Fact]
    public void Today_rejects_non_utc_time()
    {
        var local = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => BranchClock.Today(local, "Asia/Jakarta"));
    }

    [Fact]
    public void Today_rejects_unknown_timezone()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => BranchClock.Today(Utc("2026-11-20T00:00:00Z"), "Mars/Olympus"));
    }
}
