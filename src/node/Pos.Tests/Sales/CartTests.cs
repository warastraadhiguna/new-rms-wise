using Pos.Shared.Sales;

namespace Pos.Tests.Sales;

public class CartTests
{
    private static readonly Guid Water = Guid.Parse("00000000-0000-7000-8000-000000000a01");
    private static readonly Guid WaterPcs = Guid.Parse("00000000-0000-7000-8000-000000000a02");
    private static readonly Guid WaterBox = Guid.Parse("00000000-0000-7000-8000-000000000a03");
    private static readonly Guid Rice = Guid.Parse("00000000-0000-7000-8000-000000000b01");
    private static readonly Guid RiceKg = Guid.Parse("00000000-0000-7000-8000-000000000b02");
    private static readonly Guid Card = Guid.Parse("00000000-0000-7000-8000-000000000c01");
    private static readonly Guid CardPcs = Guid.Parse("00000000-0000-7000-8000-000000000c02");

    private static CartItem WaterPcsItem(decimal stock = 48m) => new(Water, WaterPcs, "AIR-600", "Air mineral", "PCS", 1m,
        AllowDecimal: false, AllowQtyMultiplier: true, [new PriceTier(1m, 4000m), new PriceTier(12m, 3500m)], stock);

    private static CartItem WaterBoxItem(decimal stock = 48m) => new(Water, WaterBox, "AIR-600", "Air mineral", "BOX", 24m,
        AllowDecimal: false, AllowQtyMultiplier: true, [new PriceTier(1m, 80000m)], stock);

    private static CartItem RiceItem() => new(Rice, RiceKg, "BERAS-KG", "Beras", "KG", 1m,
        AllowDecimal: true, AllowQtyMultiplier: true, [new PriceTier(0.1m, 14000m)], 50m);

    private static CartItem CardItem() => new(Card, CardPcs, "VCR-10", "Voucher", "PCS", 1m,
        AllowDecimal: false, AllowQtyMultiplier: false, [new PriceTier(1m, 10000m)], 100m);

    [Fact]
    public void Adding_a_product_creates_a_line_with_price_and_subtotal()
    {
        var cart = new Cart();

        var index = cart.Add(WaterPcsItem(), 3m);

        Assert.Equal(0, index);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(3m, line.Qty);
        Assert.Equal(4000m, line.Price);
        Assert.Equal(12000m, line.SubTotal);
        Assert.Equal(12000m, cart.Total);
    }

    [Fact]
    public void Adding_the_same_unit_again_increases_the_existing_line()
    {
        var cart = new Cart();
        cart.Add(RiceItem(), 1m);
        cart.Add(WaterPcsItem(), 1m);

        var index = cart.Add(WaterPcsItem(), 2m);

        Assert.Equal(1, index);
        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(3m, cart.Lines[1].Qty);
    }

    [Fact]
    public void Another_unit_of_the_same_product_is_a_separate_line()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 1m);
        cart.Add(WaterBoxItem(), 1m);

        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(84000m, cart.Total);
    }

    [Fact]
    public void Wholesale_tier_applies_when_the_qty_reaches_it()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 11m);
        Assert.Equal(4000m, cart.Lines[0].Price);

        cart.Add(WaterPcsItem(), 1m);

        Assert.Equal(3500m, cart.Lines[0].Price);
        Assert.Equal(42000m, cart.Total);
    }

    [Fact]
    public void Half_a_kilo_uses_the_one_kilo_price()
    {
        var rice = RiceItem() with { PriceTiers = [new PriceTier(1m, 14000m)] };
        var cart = new Cart();

        cart.Add(rice, 0.5m);

        Assert.Equal(14000m, cart.Lines[0].Price);
        Assert.Equal(7000m, cart.Total);
    }

    [Fact]
    public void Decimal_qty_for_weighed_goods_and_rounded_subtotal()
    {
        var cart = new Cart();
        cart.Add(RiceItem(), 0.35m);

        Assert.Equal(4900m, cart.Lines[0].SubTotal);
    }

    [Fact]
    public void Decimal_qty_for_whole_units_is_refused()
    {
        var ex = Assert.Throws<CartException>(() => new Cart().Add(WaterPcsItem(), 1.5m));
        Assert.Equal("decimal_not_allowed", ex.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_positive_qty_is_refused(string qty)
    {
        var ex = Assert.Throws<CartException>(() => new Cart().Add(WaterPcsItem(), D(qty)));
        Assert.Equal("invalid_quantity", ex.Code);
    }

    [Fact]
    public void Product_without_multiplier_is_never_merged_and_cannot_get_qty_above_one()
    {
        var cart = new Cart();
        cart.Add(CardItem(), 1m);
        cart.Add(CardItem(), 1m);

        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal("qty_multiplier_not_allowed", Assert.Throws<CartException>(() => cart.Add(CardItem(), 2m)).Code);
        Assert.Equal("qty_multiplier_not_allowed", Assert.Throws<CartException>(() => cart.SetQty(0, 3m)).Code);
    }

    [Fact]
    public void Product_without_price_is_refused()
    {
        var noPrice = WaterPcsItem() with { PriceTiers = [] };

        Assert.Equal("no_price", Assert.Throws<CartException>(() => new Cart().Add(noPrice, 1m)).Code);
    }

    [Fact]
    public void Set_qty_changes_the_line_and_its_price_tier()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 1m);

        cart.SetQty(0, 12m);

        Assert.Equal(12m, cart.Lines[0].Qty);
        Assert.Equal(42000m, cart.Total);
    }

    [Fact]
    public void Change_unit_keeps_the_qty()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 2m);

        cart.ChangeUnit(0, WaterBoxItem());

        Assert.Equal("BOX", cart.Lines[0].Item.UnitCode);
        Assert.Equal(2m, cart.Lines[0].Qty);
        Assert.Equal(160000m, cart.Total);
    }

    [Fact]
    public void Change_unit_to_a_unit_already_in_the_cart_merges_the_lines()
    {
        var cart = new Cart();
        cart.Add(WaterBoxItem(), 1m);
        cart.Add(WaterPcsItem(), 2m);

        var index = cart.ChangeUnit(1, WaterBoxItem());

        Assert.Equal(0, index);
        Assert.Equal(3m, Assert.Single(cart.Lines).Qty);
    }

    [Fact]
    public void Change_unit_to_another_product_is_refused()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 1m);

        Assert.Equal("different_product", Assert.Throws<CartException>(() => cart.ChangeUnit(0, RiceItem())).Code);
    }

    [Fact]
    public void Remove_and_clear()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(), 1m);
        cart.Add(RiceItem(), 1m);

        cart.RemoveAt(0);
        Assert.Equal("BERAS-KG", Assert.Single(cart.Lines).Item.Code);

        cart.Clear();
        Assert.True(cart.IsEmpty);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public void Stock_warning_counts_every_unit_of_a_product_in_base_units()
    {
        var cart = new Cart();
        cart.Add(WaterBoxItem(stock: 30m), 1m);   // 24 pcs
        Assert.Empty(cart.StockWarnings());

        cart.Add(WaterPcsItem(stock: 30m), 7m);    // 31 pcs > 30

        var warning = Assert.Single(cart.StockWarnings());
        Assert.Equal(Water, warning.ProductId);
        Assert.Equal(30m, warning.AvailableBaseQty);
        Assert.Equal(31m, warning.InCartBaseQty);
    }

    [Fact]
    public void Negative_stock_shows_as_a_warning_too()
    {
        var cart = new Cart();
        cart.Add(WaterPcsItem(stock: -2m), 1m);

        Assert.Single(cart.StockWarnings());
    }

    [Fact]
    public void Lines_can_be_restored()
    {
        var cart = new Cart();
        cart.Restore([new CartLine(WaterPcsItem(), 2m), new CartLine(RiceItem(), 0.5m)]);

        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(15000m, cart.Total);
    }

    [Theory]
    [InlineData("1", "4000")]
    [InlineData("11", "4000")]
    [InlineData("12", "3500")]
    [InlineData("0.5", "4000")]       // below the smallest tier: the smallest tier applies
    public void Price_for_qty_uses_the_largest_reached_tier(string qty, string? expected)
    {
        Assert.Equal(expected is null ? null : D(expected), Cart.PriceFor(WaterPcsItem().PriceTiers, D(qty)));
    }
}
