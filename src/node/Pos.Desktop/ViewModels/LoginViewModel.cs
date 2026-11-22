using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// Login with username + password only, as in the old application (RMS 2). After login the device's
/// open shift decides the next screen: own shift → main window, none → open shift, someone else's → refused.
/// </summary>
public sealed partial class LoginViewModel(INodeApi api, SessionState session, INavigator navigator) : ObservableObject
{
    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    private bool CanLogin() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorMessage = null;
        var username = Username.Trim();
        if (username.Length == 0)
        {
            ErrorMessage = "Isi username terlebih dahulu.";
            return;
        }
        if (Password.Length == 0)
        {
            ErrorMessage = "Isi password terlebih dahulu.";
            return;
        }

        IsBusy = true;
        try
        {
            var login = await api.LoginAsync(username, Password);
            session.SetLogin(login);

            // Only users who sell need a shift; store/warehouse staff without sale.create go straight to the menu.
            // A non-seller with shift.close still learns about the shift open on this PC, so it can be closed.
            if (!session.Has(Pos.Shared.Auth.Permissions.SaleCreate))
            {
                if (session.Has(Pos.Shared.Auth.Permissions.ShiftClose)
                    && await api.GetCurrentShiftAsync() is { } open && open.CashierId != login.User.Id)
                    session.ForeignShift = open;
                navigator.ShowMain();
                return;
            }

            var shift = await api.GetCurrentShiftAsync();
            if (shift is null)
            {
                navigator.ShowOpenShift();
            }
            else if (shift.CashierId != login.User.Id && session.Has(Pos.Shared.Auth.Permissions.ShiftShare))
            {
                // shift.share: sells on the shift that is open on this PC
                session.Shift = shift;
                navigator.ShowMain();
            }
            else if (shift.CashierId != login.User.Id && CanWorkWithoutSelling())
            {
                // Owner decision: admins are not refused; selling is locked, stock screens and closing shifts work.
                session.ForeignShift = shift;
                navigator.ShowMain();
            }
            else if (shift.CashierId != login.User.Id)
            {
                session.Clear();
                ErrorMessage = $"Perangkat ini masih memiliki shift terbuka ({shift.Number}) milik kasir lain. " +
                               "Shift tersebut harus ditutup terlebih dahulu.";
            }
            else
            {
                session.Shift = shift;
                navigator.ShowMain();
            }
        }
        catch (NodeApiException ex)
        {
            session.Clear();
            ErrorMessage = ex.Message;
        }
        finally
        {
            // Never keep the password in memory longer than needed.
            Password = "";
            IsBusy = false;
        }
    }

    /// <summary>Users who have work besides selling: stock screens, transfers, or closing that shift (shift.close).</summary>
    private bool CanWorkWithoutSelling() =>
        session.Has(Pos.Shared.Auth.Permissions.StockView)
        || session.Has(Pos.Shared.Auth.Permissions.StockTransfer)
        || session.Has(Pos.Shared.Auth.Permissions.ShiftClose);
}
