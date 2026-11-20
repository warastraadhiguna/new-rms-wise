using Pos.Shared.Time;

namespace Pos.Tests.Time;

public class DailyScheduleTests
{
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");   // UTC+7
    private static readonly TimeOnly TwoAm = new(2, 0);

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Later_today_in_the_branch_timezone()
    {
        // 20 Nov 10:00 WIB = 03:00 UTC; next 02:00 WIB is 21 Nov = 20 Nov 19:00 UTC
        Assert.Equal(Utc(20, 19), DailySchedule.NextUtc(Utc(20, 3), TwoAm, Jakarta));
    }

    [Fact]
    public void Before_the_time_on_the_same_local_day()
    {
        // 21 Nov 01:00 WIB = 20 Nov 18:00 UTC -> 02:00 WIB the same local day
        Assert.Equal(Utc(20, 19), DailySchedule.NextUtc(Utc(20, 18), TwoAm, Jakarta));
    }

    [Fact]
    public void Exactly_at_the_time_moves_to_the_next_day()
    {
        Assert.Equal(Utc(21, 19), DailySchedule.NextUtc(Utc(20, 19), TwoAm, Jakarta));
    }

    [Fact]
    public void Just_after_the_time_moves_to_the_next_day()
    {
        Assert.Equal(Utc(21, 19), DailySchedule.NextUtc(Utc(20, 19, 1), TwoAm, Jakarta));
    }

    [Fact]
    public void Utc_timezone()
    {
        Assert.Equal(Utc(21, 2), DailySchedule.NextUtc(Utc(20, 3), TwoAm, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Skipped_local_time_on_a_daylight_saving_day_runs_an_hour_later()
    {
        // Europe/Berlin: 29 Mar 2026 02:00 local does not exist (clocks jump to 03:00 = 01:00 UTC)
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc);

        var next = DailySchedule.NextUtc(now, new TimeOnly(2, 30), berlin);

        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), next);   // 03:30 CEST
    }

    [Fact]
    public void Rejects_local_time()
    {
        Assert.Throws<ArgumentException>(() => DailySchedule.NextUtc(new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Local), TwoAm, Jakarta));
    }

    [Theory]
    [InlineData("02:00", 2, 0)]
    [InlineData("23:30", 23, 30)]
    [InlineData(" 7:05 ", 7, 5)]
    public void Parses_hh_mm(string text, int hour, int minute)
    {
        Assert.True(DailySchedule.TryParseTime(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("2 pagi")]
    [InlineData(null)]
    public void Rejects_bad_times(string? text)
    {
        Assert.False(DailySchedule.TryParseTime(text, out _));
    }
}
