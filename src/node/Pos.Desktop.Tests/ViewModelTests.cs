using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Tests;

public class LoginViewModelTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeNavigator _navigator = new();
    private readonly SessionState _session = new();

    private LoginViewModel Vm() => new(_api, _session, _navigator);

    [Fact]
    public async Task Username_and_password_are_required()
    {
        var vm = Vm();

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("Isi username terlebih dahulu.", vm.ErrorMessage);
        Assert.Empty(_api.Calls);

        vm.Username = "kasir1";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal("Isi password terlebih dahulu.", vm.ErrorMessage);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Login_with_own_open_shift_goes_to_the_main_window()
    {
        _api.OnLogin = (_, _) => Samples.Login();
        _api.OnCurrentShift = () => Samples.Shift();
        var vm = Vm();
        vm.Username = " kasir1 ";
        vm.Password = "kasir123";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["login:kasir1", "current-shift"], _api.Calls);
        Assert.Equal(["main"], _navigator.Shown);
        Assert.Equal("Kasir Satu", _session.User!.FullName);
        Assert.Equal("TK01-SH-202611-000007", _session.Shift!.Number);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("", vm.Password);   // secrets are not kept
    }

    [Fact]
    public async Task User_without_sale_create_goes_straight_to_the_main_window_without_a_shift()
    {
        _api.OnLogin = (_, _) => Samples.LoginWith("stock.view");
        var vm = Vm();
        vm.Username = "gudang1";
        vm.Password = "rahasia";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["login:gudang1"], _api.Calls);                // no shift check
        Assert.Equal(["main"], _navigator.Shown);
        Assert.Null(_session.Shift);
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task Login_without_open_shift_goes_to_open_shift()
    {
        _api.OnLogin = (_, _) => Samples.Login();
        _api.OnCurrentShift = () => null;
        var vm = Vm();
        vm.Username = "kasir1";
        vm.Password = "kasir123";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["open-shift"], _navigator.Shown);
        Assert.Null(_session.Shift);
        Assert.True(_session.IsLoggedIn);
    }

    [Fact]
    public async Task Shift_of_another_cashier_blocks_the_login()
    {
        _api.OnLogin = (_, _) => Samples.Login();
        _api.OnCurrentShift = () => Samples.Shift(cashierId: Samples.OtherUserId);
        var vm = Vm();
        vm.Username = "kasir1";
        vm.Password = "kasir123";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Empty(_navigator.Shown);
        Assert.False(_session.IsLoggedIn);
        Assert.Equal(
            "Perangkat ini masih memiliki shift terbuka (TK01-SH-202611-000007) milik kasir lain. Shift tersebut harus ditutup terlebih dahulu.",
            vm.ErrorMessage);
    }

    [Fact]
    public async Task Admin_is_let_in_with_the_foreign_shift_and_selling_locked()
    {
        _api.OnLogin = (_, _) => Samples.LoginWith("stock.view", "stock.transfer", "sale.create");
        _api.OnCurrentShift = () => Samples.Shift(cashierId: Samples.OtherUserId) with { CashierName = "Kasir Satu" };
        var vm = Vm();
        vm.Username = "admintoko";
        vm.Password = "admintoko123";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["main"], _navigator.Shown);
        Assert.Null(_session.Shift);
        Assert.Equal("Kasir Satu", _session.ForeignShift?.CashierName);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task A_supervisor_with_shift_close_is_let_in_to_close_the_foreign_shift()
    {
        _api.OnLogin = (_, _) => Samples.LoginWith("shift.close");
        _api.OnCurrentShift = () => Samples.Shift(cashierId: Samples.OtherUserId);
        var vm = Vm();
        vm.Username = "spv";
        vm.Password = "x";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["main"], _navigator.Shown);
        Assert.NotNull(_session.ForeignShift);
    }

    [Fact]
    public async Task Shift_share_sells_on_the_open_shift_of_another_cashier()
    {
        _api.OnLogin = (_, _) => Samples.LoginWith("shift.share", "sale.create");
        var shift = Samples.Shift(cashierId: Samples.OtherUserId);
        _api.OnCurrentShift = () => shift;
        var vm = Vm();
        vm.Username = "kasir2";
        vm.Password = "x";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(["main"], _navigator.Shown);
        Assert.Equal(shift, _session.Shift);
        Assert.Null(_session.ForeignShift);
    }

    [Fact]
    public async Task Api_error_is_shown_and_the_form_is_usable_again()
    {
        _api.OnLogin = (_, _) => throw new NodeApiException("Username atau password salah.", "invalid_credentials", 401);
        var vm = Vm();
        vm.Username = "kasir1";
        vm.Password = "salah";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("Username atau password salah.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.True(vm.LoginCommand.CanExecute(null));
        Assert.Empty(_navigator.Shown);
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public void Login_is_username_and_password_only()
    {
        // The owner's decision: same flow as RMS 2, no PIN mode on the desktop.
        var properties = typeof(LoginViewModel).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Pin", properties);
        Assert.DoesNotContain("UsePin", properties);
        Assert.DoesNotContain(typeof(INodeApi).GetMethods(), m => m.Name.Contains("Pin", StringComparison.Ordinal));
    }
}

public class OpenShiftViewModelTests
{
    private readonly FakeNodeApi _api = new();
    private readonly FakeNavigator _navigator = new();
    private readonly SessionState _session = new();

    public OpenShiftViewModelTests() => _session.SetLogin(Samples.Login());

    private OpenShiftViewModel Vm() => new(_api, _session, _navigator);

    [Fact]
    public void Shows_who_opens_the_shift()
    {
        Assert.Equal("Kasir Satu", Vm().CashierName);
    }

    [Theory]
    [InlineData("200000", "200000")]
    [InlineData("200.000", "200000")]       // Indonesian thousands separator
    [InlineData("Rp 150.000", "150000")]
    [InlineData("0", "0")]
    [InlineData("1.500,50", "1500.50")]
    public async Task Opening_cash_is_parsed_and_sent(string input, string expected)
    {
        _api.OnOpenShift = cash => Samples.Shift() with { OpeningCash = cash };
        var vm = Vm();
        vm.OpeningCashText = input;

        await vm.OpenShiftCommand.ExecuteAsync(null);

        Assert.Equal($"open-shift:{decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)}", Assert.Single(_api.Calls));
        Assert.Equal(["main"], _navigator.Shown);
        Assert.NotNull(_session.Shift);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5000")]
    public async Task Invalid_opening_cash_is_refused(string input)
    {
        var vm = Vm();
        vm.OpeningCashText = input;

        await vm.OpenShiftCommand.ExecuteAsync(null);

        Assert.Equal("Masukkan modal awal berupa angka, misalnya 200.000.", vm.ErrorMessage);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Api_error_is_shown()
    {
        _api.OnOpenShift = _ => throw new NodeApiException("Perangkat ini masih punya shift terbuka.", "shift_already_open", 409);
        var vm = Vm();
        vm.OpeningCashText = "100000";

        await vm.OpenShiftCommand.ExecuteAsync(null);

        Assert.Equal("Perangkat ini masih punya shift terbuka.", vm.ErrorMessage);
        Assert.Empty(_navigator.Shown);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Cancel_logs_out()
    {
        var vm = Vm();

        vm.CancelCommand.Execute(null);

        Assert.False(_session.IsLoggedIn);
        Assert.Equal(["login"], _navigator.Shown);
    }
}

public class MainViewModelTests
{
    [Fact]
    public void Status_bar_shows_cashier_shift_and_node()
    {
        var session = new SessionState();
        session.SetLogin(Samples.Login());
        session.Shift = Samples.Shift();

        var vm = new MainViewModel(session);

        Assert.Equal("Kasir Satu", vm.CashierName);
        Assert.Equal("TK01-SH-202611-000007", vm.ShiftNumber);
        Assert.Equal("TK01 - Toko Dev", vm.NodeName);
    }
}

public class MoneyInputTests
{
    [Theory]
    [InlineData("200000", "200000")]
    [InlineData("200.000", "200000")]
    [InlineData("1.234.567", "1234567")]
    [InlineData("1.500,50", "1500.50")]
    [InlineData("Rp 75.000", "75000")]
    [InlineData("  10  ", "10")]
    public void Parses_indonesian_amounts(string input, string expected)
    {
        Assert.True(MoneyInput.TryParse(input, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12a")]
    [InlineData("-1")]
    public void Rejects_invalid_or_negative(string input)
    {
        Assert.False(MoneyInput.TryParse(input, out _));
    }
}
