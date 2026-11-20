using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// Login with username + password, or username + PIN (quick cashier change). After login the device's
/// open shift decides the next screen: own shift → main window, none → open shift, someone else's → refused.
/// </summary>
public sealed partial class LoginViewModel(INodeApi api, SessionState session, INavigator navigator) : ObservableObject
{
    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _pin = "";

    [ObservableProperty]
    private bool _usePin;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    partial void OnUsePinChanged(bool value) => ErrorMessage = null;

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
        if (UsePin ? Pin.Length == 0 : Password.Length == 0)
        {
            ErrorMessage = UsePin ? "Isi PIN terlebih dahulu." : "Isi password terlebih dahulu.";
            return;
        }

        IsBusy = true;
        try
        {
            var login = UsePin ? await api.PinLoginAsync(username, Pin) : await api.LoginAsync(username, Password);
            session.SetLogin(login);

            var shift = await api.GetCurrentShiftAsync();
            if (shift is null)
            {
                navigator.ShowOpenShift();
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
            // Never keep secrets in memory longer than needed.
            Password = "";
            Pin = "";
            IsBusy = false;
        }
    }
}
