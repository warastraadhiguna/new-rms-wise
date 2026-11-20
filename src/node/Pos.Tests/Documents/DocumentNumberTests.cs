using Pos.Shared.Documents;

namespace Pos.Tests.Documents;

public class DocumentNumberTests
{
    [Theory]
    [InlineData("TK01", "SL", "202611", 123, "TK01-SL-202611-000123")]
    [InlineData("GD01", "SH", "202701", 1, "GD01-SH-202701-000001")]
    [InlineData("TK01", "SL", "202611", 999999, "TK01-SL-202611-999999")]
    [InlineData("TK01", "SL", "202611", 1000000, "TK01-SL-202611-1000000")]   // grows, never wraps
    public void Format_builds_node_type_period_number(string node, string type, string period, int number, string expected)
    {
        Assert.Equal(expected, DocumentNumber.Format(node, type, period, number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Format_rejects_non_positive_number(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentNumber.Format("TK01", "SL", "202611", number));
    }

    [Theory]
    [InlineData("", "SL", "202611")]
    [InlineData("tk01", "SL", "202611")]
    [InlineData("TK-01", "SL", "202611")]
    [InlineData("TK01", "", "202611")]
    [InlineData("TK01", "sl", "202611")]
    [InlineData("TK01", "SL", "2026-11")]
    [InlineData("TK01", "SL", "202613")]
    public void Format_rejects_malformed_parts(string node, string type, string period)
    {
        Assert.Throws<ArgumentException>(() => DocumentNumber.Format(node, type, period, 1));
    }

    [Theory]
    [InlineData("2026-11-30T16:59:59Z", "Asia/Jakarta", "202611")]   // 23:59:59 WIB
    [InlineData("2026-11-30T17:00:00Z", "Asia/Jakarta", "202612")]   // 00:00 WIB, next month
    [InlineData("2026-12-31T17:00:00Z", "Asia/Jakarta", "202701")]   // new year in WIB
    [InlineData("2026-11-30T15:00:00Z", "Asia/Jayapura", "202612")]  // WIT is UTC+9
    public void Period_uses_branch_timezone(string utc, string timeZone, string expected)
    {
        Assert.Equal(expected, DocumentNumber.Period(Utc(utc), timeZone));
    }

    [Fact]
    public void Period_rejects_non_utc_time()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentNumber.Period(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Local), "Asia/Jakarta"));
    }

    [Fact]
    public void Document_type_codes_are_valid()
    {
        Assert.All(DocumentTypes.All, code => DocumentNumber.Format("TK01", code, "202611", 1));
        Assert.Equal(DocumentTypes.All.Count, DocumentTypes.All.Distinct().Count());
    }
}
