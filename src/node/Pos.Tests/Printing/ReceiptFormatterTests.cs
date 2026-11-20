using Pos.Shared.Printing;

namespace Pos.Tests.Printing;

public class ReceiptFormatterTests
{
    /// <summary>Left text + right text filling exactly <paramref name="width"/> columns.</summary>
    private static string R(string left, string right, int width = 48) => left + right.PadLeft(width - left.Length);

    private static readonly ReceiptPayment Cash20k = new("TUNAI", IsCash: true, IsCredit: false, Amount: 19500m, Tendered: 20000m, Change: 500m);

    private static ReceiptData Sample(
        IReadOnlyList<ReceiptItem>? items = null, decimal subtotal = 19500m, decimal discount = 0m, decimal? discountPct = null,
        decimal rounding = 0m, decimal? total = null, IReadOnlyList<ReceiptPayment>? payments = null, string? customer = null,
        DateOnly? dueDate = null) => new(
        HeaderLines: ["TOKO DEV", "Jl. Merdeka No. 1", "Jakarta"],
        FooterLines: [ReceiptDefaults.FooterLine],
        Number: "TK01-SL-202611-000123",
        SoldAtLocal: new DateTime(2026, 11, 20, 18, 5, 0),
        CashierName: "Kasir Satu",
        CustomerName: customer,
        Items: items ??
        [
            new ReceiptItem("Air mineral 600 ml", 3m, "PCS", 4000m, 12000m),
            new ReceiptItem("Amplop lebaran", 1m, "PAK", 7500m, 7500m),
        ],
        Subtotal: subtotal,
        DiscountAmount: discount,
        DiscountPct: discountPct,
        RoundingAmount: rounding,
        Total: total ?? subtotal - discount + rounding,
        Payments: payments ?? [Cash20k],
        DueDate: dueDate);

    [Fact]
    public void Width_48_matches_the_rms2_layout()
    {
        var lines = ReceiptFormatter.Format(Sample(), 48);

        string[] expected =
        [
            "                    TOKO DEV",
            "               Jl. Merdeka No. 1",
            "                    Jakarta",
            "------------------------------------------------",
            "No     : TK01-SL-202611-000123",
            "Tanggal: 20/11/2026 18:05",
            "Kasir  : Kasir Satu",
            "------------------------------------------------",
            "Air mineral 600 ml",
            R("  3 PCS x 4.000", "12.000"),
            "Amplop lebaran",
            R("  1 PAK x 7.500", "7.500"),
            "------------------------------------------------",
            R("SubTotal", "19.500"),
            R("Grand Total", "19.500"),
            R("Cara Bayar", "TUNAI"),
            R("Bayar", "20.000"),
            R("Kembali", "500"),
            "------------------------------------------------",
            "         Terimakasih Atas Kunjungannya",
        ];
        Assert.Equal(expected, lines);
    }

    [Fact]
    public void Width_32_for_58mm_paper()
    {
        var lines = ReceiptFormatter.Format(Sample(), ReceiptFormatter.Width58mm);

        Assert.All(lines, l => Assert.True(l.Length <= 32, $"'{l}' is {l.Length} wide"));
        Assert.Equal(new string('-', 32), lines[3]);
        Assert.Contains(R("  3 PCS x 4.000", "12.000", 32), lines);
        Assert.Contains(R("Grand Total", "19.500", 32), lines);
        Assert.Contains(" Terimakasih Atas Kunjungannya", lines);
    }

    [Fact]
    public void Long_names_take_two_lines_at_most_and_wrap_on_words()
    {
        var item = new ReceiptItem("Minyak goreng kemasan pouch refill premium kualitas ekspor 2 liter isi ulang", 1m, "PCS", 38000m, 38000m);

        var lines = ReceiptFormatter.Format(Sample(items: [item], subtotal: 38000m, payments: [Cash20k with { Tendered = 50000m }]), 32);

        var at = lines.ToList().IndexOf("Minyak goreng kemasan pouch");
        Assert.True(at > 0);
        Assert.Equal("refill premium kualitas ekspor 2", lines[at + 1]);
        Assert.Equal(R("  1 PCS x 38.000", "38.000", 32), lines[at + 2]);
    }

    [Fact]
    public void A_name_without_spaces_is_cut_hard()
    {
        var item = new ReceiptItem(new string('X', 70), 1m, "PCS", 1000m, 1000m);

        var lines = ReceiptFormatter.Format(Sample(items: [item], subtotal: 1000m), 32);

        var at = lines.ToList().IndexOf(new string('X', 32));
        Assert.Equal(new string('X', 32), lines[at + 1]);
        Assert.StartsWith("  1 PCS", lines[at + 2]);
    }

    [Fact]
    public void Large_numbers_move_the_subtotal_to_its_own_line_when_needed()
    {
        var item = new ReceiptItem("Mesin kasir", 1250.5m, "PCS", 1250000m, 1563125000m);

        var narrow = ReceiptFormatter.Format(Sample(items: [item], subtotal: 1563125000m,
            payments: [Cash20k with { Amount = 1563125000m, Tendered = 1563125000m, Change = 0m }]), 32);
        var wide = ReceiptFormatter.Format(Sample(items: [item], subtotal: 1563125000m,
            payments: [Cash20k with { Amount = 1563125000m, Tendered = 1563125000m, Change = 0m }]), 48);

        var at = narrow.ToList().IndexOf("  1.250,5 PCS x 1.250.000");
        Assert.True(at > 0, string.Join("\n", narrow));
        Assert.Equal("1.563.125.000".PadLeft(32), narrow[at + 1]);
        Assert.Contains(R("  1.250,5 PCS x 1.250.000", "1.563.125.000"), wide);
        Assert.Contains(R("Grand Total", "1.563.125.000", 32), narrow);
        Assert.All(narrow, l => Assert.True(l.Length <= 32));
    }

    [Fact]
    public void Amount_discount_is_potongan_and_percent_discount_is_diskon()
    {
        var amount = ReceiptFormatter.Format(Sample(discount: 1500m), 48);
        var percent = ReceiptFormatter.Format(Sample(discount: 1950m, discountPct: 10m), 48);
        var fraction = ReceiptFormatter.Format(Sample(discount: 487.5m, discountPct: 2.5m, rounding: 37.5m), 48);

        Assert.Contains(R("Potongan", "1.500"), amount);
        Assert.Contains(R("Grand Total", "18.000"), amount);
        Assert.Contains(R("Diskon 10%", "1.950"), percent);
        Assert.Contains(R("Diskon 2,5%", "487,5"), fraction);
        Assert.DoesNotContain(ReceiptFormatter.Format(Sample(), 48), l => l.StartsWith("Potongan") || l.StartsWith("Diskon"));
    }

    [Fact]
    public void Cash_rounding_shows_only_when_not_zero()
    {
        var rounded = ReceiptFormatter.Format(Sample(subtotal: 19450m, rounding: 50m), 48);

        Assert.Contains(R("Pembulatan", "50"), rounded);
        Assert.Contains(R("Grand Total", "19.500"), rounded);
        Assert.Contains(R("Pembulatan", "-50"), ReceiptFormatter.Format(Sample(subtotal: 19550m, rounding: -50m), 48));
        Assert.DoesNotContain(ReceiptFormatter.Format(Sample(), 48), l => l.StartsWith("Pembulatan"));
    }

    [Fact]
    public void Credit_sale_without_dp_shows_uang_muka_0_sisa_piutang_and_due_date()
    {
        var credit = new ReceiptPayment("KREDIT", IsCash: false, IsCredit: true, Amount: 19500m, Tendered: null, Change: null);

        var lines = ReceiptFormatter.Format(Sample(payments: [credit], customer: "C000001 - Budi Pelanggan", dueDate: new DateOnly(2026, 12, 4)), 48);

        Assert.Contains("Member : C000001 - Budi Pelanggan", lines);
        Assert.Contains(R("Cara Bayar", "KREDIT"), lines);
        Assert.Contains(R("Uang Muka", "0"), lines);
        Assert.Contains(R("Sisa Piutang", "19.500"), lines);
        Assert.Contains(R("Jatuh Tempo", "04/12/2026"), lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Bayar", StringComparison.Ordinal) || l.StartsWith("Kembali", StringComparison.Ordinal));
        // order: Grand Total, Cara Bayar, Uang Muka, Sisa Piutang, Jatuh Tempo
        var at = lines.ToList().IndexOf(R("Cara Bayar", "KREDIT"));
        Assert.Equal(R("Uang Muka", "0"), lines[at + 1]);
        Assert.Equal(R("Sisa Piutang", "19.500"), lines[at + 2]);
        Assert.Equal(R("Jatuh Tempo", "04/12/2026"), lines[at + 3]);
    }

    [Fact]
    public void Credit_sale_with_dp_shows_the_dp_as_uang_muka_and_the_rest_as_sisa_piutang()
    {
        ReceiptPayment[] payments =
        [
            new("TUNAI", IsCash: true, IsCredit: false, Amount: 5000m, Tendered: 5000m, Change: 0m),
            new("KREDIT", IsCash: false, IsCredit: true, Amount: 14500m, Tendered: null, Change: null),
        ];

        var lines = ReceiptFormatter.Format(Sample(payments: payments, customer: "C000001 - Budi", dueDate: new DateOnly(2026, 12, 4)), 48);

        Assert.Contains(R("Cara Bayar", "KREDIT"), lines);
        Assert.Contains(R("Uang Muka", "5.000"), lines);
        Assert.Contains(R("Sisa Piutang", "14.500"), lines);
        Assert.Contains(R("Jatuh Tempo", "04/12/2026"), lines);
    }

    [Fact]
    public void Reprint_adds_cetak_ulang_right_under_the_header()
    {
        var lines = ReceiptFormatter.Format(Sample(), 48, reprint: true);

        Assert.Equal("                  CETAK ULANG", lines[3]);
        Assert.Equal(new string('-', 48), lines[4]);
        Assert.DoesNotContain(ReceiptFormatter.Format(Sample(), 48), l => l.Contains("CETAK ULANG"));
    }

    [Fact]
    public void Long_header_and_footer_lines_wrap_and_stay_centered()
    {
        var data = Sample() with
        {
            HeaderLines = ["TOKO SERBA ADA SEJAHTERA SELALU JAYA ABADI"],
            FooterLines = ["Barang yang sudah dibeli tidak dapat ditukar"],
        };

        var lines = ReceiptFormatter.Format(data, 32);

        Assert.Equal("TOKO SERBA ADA SEJAHTERA SELALU", lines[0]);
        Assert.Equal("           JAYA ABADI", lines[1]);
        Assert.Equal(" Barang yang sudah dibeli tidak", lines[^2]);
        Assert.Equal("         dapat ditukar", lines[^1]);
    }

    [Fact]
    public void Empty_header_and_footer_print_nothing_there()
    {
        var lines = ReceiptFormatter.Format(Sample() with { HeaderLines = [], FooterLines = [] }, 48);

        Assert.Equal(new string('-', 48), lines[0]);
        Assert.Equal(new string('-', 48), lines[^1]);
    }

    [Fact]
    public void Several_payments_are_joined()
    {
        var lines = ReceiptFormatter.Format(Sample(payments:
        [
            new ReceiptPayment("QRIS", false, false, 10000m, null, null),
            new ReceiptPayment("TUNAI", true, false, 9500m, 10000m, 500m),
        ]), 48);

        Assert.Contains(R("Cara Bayar", "QRIS + TUNAI"), lines);
        Assert.Contains(R("Bayar", "20.000"), lines);
        Assert.Contains(R("Kembali", "500"), lines);
    }

    [Theory]
    [InlineData(23)]
    [InlineData(0)]
    public void Rejects_too_narrow_paper(int width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReceiptFormatter.Format(Sample(), width));

    [Theory]
    [InlineData(32)]
    [InlineData(42)]
    [InlineData(48)]
    public void No_line_is_wider_than_the_paper(int width)
    {
        var item = new ReceiptItem("Produk dengan nama yang sangat panjang sekali untuk diuji", 99999.9999m, "BUNGKUS", 999999999m, 99999999890000m);
        var lines = ReceiptFormatter.Format(Sample(items: [item], subtotal: 99999999890000m, discount: 12345678m, rounding: -90m,
            customer: "C000001 - Pelanggan dengan nama yang sangat panjang"), width, reprint: true);

        Assert.All(lines, l => Assert.True(l.Length <= width, $"'{l}' is {l.Length} wide"));
    }
}
