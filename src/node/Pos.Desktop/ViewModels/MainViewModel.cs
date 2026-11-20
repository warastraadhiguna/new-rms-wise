using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// The cashier window: hosts "Form Penjualan" (opened at start, closed with Esc or the tab's ×,
/// reopened with F2); the status bar shows who works on which shift and node.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly Func<SalesViewModel>? _salesFactory;

    public MainViewModel(SessionState session, Func<SalesViewModel>? salesFactory = null)
    {
        _session = session;
        _salesFactory = salesFactory;
        OpenSalesForm();
    }

    public string CashierName => _session.User?.FullName ?? "";

    public string ShiftNumber => _session.Shift?.Number ?? "";

    public string NodeName => _session.Node is { } node ? $"{node.Code} - {node.Name}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSalesFormOpen))]
    private SalesViewModel? _salesForm;

    public bool IsSalesFormOpen => SalesForm is not null;

    /// <summary>F2 "Form Penjualan".</summary>
    [RelayCommand]
    private void OpenSalesForm()
    {
        if (SalesForm is not null || _salesFactory is null)
            return;

        var form = _salesFactory();
        form.CloseRequested += (_, _) => SalesForm = null;
        SalesForm = form;
    }

    /// <summary>The tab's ×: same as Esc in the form (asks when the cart is not empty).</summary>
    [RelayCommand]
    private void CloseSalesForm() => SalesForm?.EscapeCommand.Execute(null);
}
