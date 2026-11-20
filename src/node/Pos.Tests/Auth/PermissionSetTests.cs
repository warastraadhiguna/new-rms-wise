using Pos.Shared.Auth;

namespace Pos.Tests.Auth;

public class PermissionSetTests
{
    [Fact]
    public void Merges_permissions_of_several_roles()
    {
        var set = PermissionSet.FromRoleJson(["[\"sale.create\",\"shift.open\"]", "[\"shift.close\",\"sale.create\"]"]);

        Assert.Equal(["sale.create", "shift.close", "shift.open"], set.Names);
        Assert.True(set.Has(Permissions.SaleCreate));
        Assert.False(set.Has("sale.void"));
    }

    [Fact]
    public void Wildcard_grants_everything()
    {
        var set = PermissionSet.FromRoleJson(["[\"*\"]"]);

        Assert.True(set.Has(Permissions.SaleCreate));
        Assert.True(set.Has("anything.at.all"));
    }

    [Fact]
    public void Null_and_empty_role_permissions_grant_nothing()
    {
        var set = PermissionSet.FromRoleJson([null, "[]", "null"]);

        Assert.Empty(set.Names);
        Assert.False(set.Has(Permissions.SaleCreate));
    }

    [Fact]
    public void Names_are_case_insensitive_and_trimmed()
    {
        var set = PermissionSet.FromRoleJson(["[\" Sale.Create \"]"]);

        Assert.True(set.Has("sale.create"));
        Assert.Equal(["sale.create"], set.Names);
    }

    [Fact]
    public void Parameterised_permissions_are_kept()
    {
        var set = PermissionSet.FromRoleJson(["[\"discount.max:10\"]"]);

        Assert.Contains("discount.max:10", set.Names);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void Malformed_role_permissions_are_rejected(string json)
    {
        Assert.Throws<FormatException>(() => PermissionSet.FromRoleJson([json]));
    }

    [Fact]
    public void Intersect_keeps_only_permissions_present_in_both()
    {
        var token = PermissionSet.FromNames(["sale.create", "shift.open", "shift.close"]);
        var current = PermissionSet.FromNames(["shift.open", "shift.close", "sale.void"]);

        Assert.Equal(["shift.close", "shift.open"], token.Intersect(current).Names);
    }

    [Fact]
    public void Intersect_with_wildcard_on_one_side_keeps_the_other_side()
    {
        var all = PermissionSet.FromNames(["*"]);
        var some = PermissionSet.FromNames(["sale.create"]);

        Assert.Equal(["sale.create"], all.Intersect(some).Names);
        Assert.Equal(["sale.create"], some.Intersect(all).Names);
        Assert.Equal(["*"], all.Intersect(all).Names);
    }

    [Fact]
    public void Intersect_with_nothing_is_nothing()
    {
        Assert.Empty(PermissionSet.FromNames(["*"]).Intersect(PermissionSet.FromNames([])).Names);
    }

    [Fact]
    public void Shift_share_permission_name()
    {
        Assert.Equal("shift.share", Permissions.ShiftShare);
    }

    [Fact]
    public void From_names_round_trip()
    {
        var set = PermissionSet.FromNames(["shift.open", "SHIFT.OPEN", "sale.create"]);

        Assert.Equal(["sale.create", "shift.open"], set.Names);
    }
}
