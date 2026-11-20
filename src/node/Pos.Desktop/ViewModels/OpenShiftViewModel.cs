using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>Opens the cashier's shift with the opening cash (modal awal) counted in the drawer.</summary>
public sealed partial class OpenShiftViewModel(INodeApi api, SessionState session, INavigator navigator) : ObservableObject
{
    public string CashierName => session.User?.FullName ?? "";

    [ObservableProperty]
    private string _openingCashText = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenShiftCommand))]
    private bool _isBusy;

    private bool CanOpen() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenShiftAsync()
    {
        ErrorMessage = null;
        if (!MoneyInput.TryParse(OpeningCashText, out var openingCash))
        {
            ErrorMessage = "Masukkan modal awal berupa angka, misalnya 200.000.";
            return;
        }

        IsBusy = true;
        try
        {
            session.Shift = await api.OpenShiftAsync(openingCash);
            navigator.ShowMain();
        }
        catch (NodeApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        session.Clear();
        navigator.ShowLogin();
    }
}
