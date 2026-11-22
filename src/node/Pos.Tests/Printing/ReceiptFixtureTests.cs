using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Shared.Printing;

namespace Pos.Tests.Printing;

/// <summary>
/// docs/fixtures/receipt-format.json is shared with the HQ receipt preview (PHP port of <see cref="ReceiptFormatter"/>):
/// both must print <c>expected</c> for <c>data</c>. C# is the reference: with POS_UPDATE_FIXTURES=1 this test writes
/// its own output into the file instead of comparing (then run the PHP test).
/// </summary>
public class ReceiptFixtureTests
{
    private static readonly string FixturePath = Path.Combine(RepoRoot(), "docs", "fixtures", "receipt-format.json");

    [Fact]
    public void ReceiptFixture_matches_the_formatter()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath))!;
        var cases = root["cases"]!.AsArray();
        var update = Environment.GetEnvironmentVariable("POS_UPDATE_FIXTURES") == "1";

        foreach (var c in cases)
        {
            var actual = ReceiptFormatter.Format(Data(c!["data"]!), c["width"]!.GetValue<int>(), c["reprint"]!.GetValue<bool>());
            if (update)
                c["expected"] = new JsonArray(actual.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            else
                Assert.Equal(c["expected"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(), actual.ToList());
        }

        if (update)
        {
            File.WriteAllText(FixturePath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
        }
        Assert.NotEmpty(cases);
    }

    private static ReceiptData Data(JsonNode d) => new(
        Strings(d["header"]),
        Strings(d["footer"]),
        d["number"]!.GetValue<string>(),
        DateTime.ParseExact(d["sold_at_local"]!.GetValue<string>(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        d["cashier"]!.GetValue<string>(),
        d["customer"]?.GetValue<string>(),
        d["items"]!.AsArray().Select(i => new ReceiptItem(
            i!["name"]!.GetValue<string>(), Dec(i["qty"])!.Value, i["unit"]!.GetValue<string>(), Dec(i["unit_price"])!.Value, Dec(i["line_total"])!.Value)).ToList(),
        Dec(d["subtotal"])!.Value,
        Dec(d["discount_amount"])!.Value,
        Dec(d["discount_pct"]),
        Dec(d["rounding"])!.Value,
        Dec(d["total"])!.Value,
        d["payments"]!.AsArray().Select(p => new ReceiptPayment(
            p!["method"]!.GetValue<string>(), p["is_cash"]!.GetValue<bool>(), p["is_credit"]!.GetValue<bool>(),
            Dec(p["amount"])!.Value, Dec(p["tendered"]), Dec(p["change"]))).ToList(),
        d["due_date"] is { } due ? DateOnly.ParseExact(due.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture) : null);

    private static List<string> Strings(JsonNode? n) => n?.AsArray().Select(s => s!.GetValue<string>()).ToList() ?? [];

    private static decimal? Dec(JsonNode? n) => n is null ? null : decimal.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "database", "migrations")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (with database/migrations) not found above " + AppContext.BaseDirectory);
    }
}
