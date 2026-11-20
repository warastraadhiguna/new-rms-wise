using System.Text.Json;
using Pos.Shared.Sync;

namespace Pos.Tests.Sync;

public class PosJsonTests
{
    private sealed record Sample(
        Guid Id,
        decimal UnitPrice,
        decimal? Discount,
        DateTime SoldAt,
        DateTime? ClosedAt,
        DateOnly? ExpiryDate,
        string? Notes);

    private static readonly Guid Id = Guid.Parse("01930000-0000-7000-8000-000000000001");

    [Fact]
    public void Writes_snake_case_decimal_strings_and_iso_utc()
    {
        var json = JsonSerializer.Serialize(
            new Sample(Id, 12500m, null, Utc("2026-11-20T03:15:00.123Z"), null, new DateOnly(2027, 1, 1), "x"),
            PosJson.Options);

        Assert.Equal(
            """{"id":"01930000-0000-7000-8000-000000000001","unit_price":"12500.0000","discount":null,"sold_at":"2026-11-20T03:15:00.123Z","closed_at":null,"expiry_date":"2027-01-01","notes":"x"}""",
            json);
    }

    [Theory]
    [InlineData("0", "0.0000")]
    [InlineData("-3.5", "-3.5000")]
    [InlineData("0.00005", "0.0001")]        // storage scale, midpoint away from zero
    [InlineData("123456789012.1234", "123456789012.1234")]
    public void Decimals_are_written_with_four_decimals(string value, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(D(value), PosJson.Options));
    }

    [Fact]
    public void Decimals_are_written_with_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("id-ID");
            Assert.Equal("\"1234.5000\"", JsonSerializer.Serialize(1234.5m, PosJson.Options));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("\"12500.0000\"", "12500")]
    [InlineData("\"0.25\"", "0.25")]
    [InlineData("3", "3")]                  // plain JSON numbers are accepted when reading
    [InlineData("1.5", "1.5")]
    public void Decimals_are_read_from_strings_and_numbers(string json, string expected)
    {
        Assert.Equal(D(expected), JsonSerializer.Deserialize<decimal>(json, PosJson.Options));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"\"")]
    public void Malformed_decimal_strings_are_rejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal>(json, PosJson.Options));
    }

    [Fact]
    public void Non_utc_datetime_is_rejected_when_writing()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
            new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Local), PosJson.Options));
    }

    [Theory]
    [InlineData("\"2026-11-20T03:15:00.123Z\"")]
    [InlineData("\"2026-11-20T10:15:00.123+07:00\"")]
    public void Datetime_is_read_as_utc(string json)
    {
        var value = JsonSerializer.Deserialize<DateTime>(json, PosJson.Options);

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(Utc("2026-11-20T03:15:00.123Z"), value);
    }

    [Fact]
    public void Round_trip()
    {
        var original = new Sample(Id, 0.25m, -1.5m, Utc("2026-11-20T03:15:00.123Z"), Utc("2026-11-20T04:00:00Z"), null, null);

        var copy = JsonSerializer.Deserialize<Sample>(JsonSerializer.Serialize(original, PosJson.Options), PosJson.Options);

        Assert.Equal(original, copy);
    }
}
