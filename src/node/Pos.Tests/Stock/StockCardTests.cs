using Pos.Shared.Stock;
using Pos.Shared.Time;

namespace Pos.Tests.Stock;

public class StockCardTests
{
    [Theory]
    [InlineData("SALE", false, "Penjualan")]
    [InlineData("SALE_RETURN", false, "Retur")]
    [InlineData("GOODS_RECEIPT", false, "Terima Barang")]
    [InlineData("DELIVERY", false, "Mutasi")]
    [InlineData("LOCATION_TRANSFER", false, "Mutasi")]
    [InlineData("ADJUSTMENT", false, "Penyesuaian")]
    [InlineData("ADJUSTMENT", true, "Opname")]
    public void Document_type_labels(string docType, bool fromStockCount, string expected) =>
        Assert.Equal(expected, StockDocTypes.Label(docType, fromStockCount));

    [Fact]
    public void Unknown_type_shows_the_code() => Assert.Equal("SOMETHING_NEW", StockDocTypes.Label("SOMETHING_NEW", false));

    [Fact]
    public void Branch_day_starts_at_local_midnight_in_utc()
    {
        var jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");   // UTC+7

        var start = BranchClock.StartOfDayUtc(new DateOnly(2026, 11, 22), jakarta);

        Assert.Equal(new DateTime(2026, 11, 21, 17, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void Date_range_covers_whole_local_days()
    {
        var jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");

        var (from, to) = BranchClock.DayRangeUtc(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22), jakarta);

        Assert.Equal(new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 11, 22, 17, 0, 0, DateTimeKind.Utc), to);    // exclusive: start of 23 Nov
    }

    [Fact]
    public void One_day_range_and_reversed_range()
    {
        var utc = TimeZoneInfo.Utc;
        var day = new DateOnly(2026, 11, 22);

        Assert.Equal((new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc)),
            BranchClock.DayRangeUtc(day, day, utc));
        Assert.Throws<ArgumentException>(() => BranchClock.DayRangeUtc(day, day.AddDays(-1), utc));
    }

    [Fact]
    public void Day_starting_inside_a_daylight_saving_gap_uses_the_first_valid_time()
    {
        // America/Santiago skips 00:00 -> 01:00 on 6 Sep 2026
        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

        var start = BranchClock.StartOfDayUtc(new DateOnly(2026, 9, 6), santiago);

        Assert.Equal(new DateOnly(2026, 9, 6), DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(start, santiago)));
    }
}
