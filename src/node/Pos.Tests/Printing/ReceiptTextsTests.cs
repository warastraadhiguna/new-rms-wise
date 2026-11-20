using Pos.Shared.Printing;

namespace Pos.Tests.Printing;

public class ReceiptTextsTests
{
    [Fact]
    public void Header_is_store_name_address_city_and_phone_in_that_order()
    {
        var lines = ReceiptTexts.Header("TOKO DEV", "Jl. Merdeka No. 1", "Jakarta", "021-5550123");

        Assert.Equal(["TOKO DEV", "Jl. Merdeka No. 1", "Jakarta", "Telp. 021-5550123"], lines);
    }

    [Fact]
    public void Empty_header_fields_are_skipped_and_text_is_trimmed()
    {
        Assert.Equal(["TOKO DEV", "Bandung"], ReceiptTexts.Header("  TOKO DEV ", "", " Bandung", "   "));
        Assert.Empty(ReceiptTexts.Header(null, null, null, null));
    }

    [Fact]
    public void Footer_has_two_separately_editable_lines()
    {
        Assert.Equal(["Terimakasih Atas Kunjungannya", "Sampai Jumpa Kembali"],
            ReceiptTexts.Footer("Terimakasih Atas Kunjungannya", "Sampai Jumpa Kembali"));
    }

    [Theory]
    [InlineData("", "Sampai Jumpa Kembali", "Sampai Jumpa Kembali")]
    [InlineData("Terimakasih", null, "Terimakasih")]
    [InlineData("Terimakasih", "  ", "Terimakasih")]
    public void Empty_footer_lines_are_not_printed(string? first, string? second, string expected)
    {
        Assert.Equal([expected], ReceiptTexts.Footer(first, second));
    }

    [Fact]
    public void No_footer_at_all()
    {
        Assert.Empty(ReceiptTexts.Footer("", null));
    }
}
