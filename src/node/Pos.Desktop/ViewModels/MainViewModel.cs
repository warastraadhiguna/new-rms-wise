using CommunityToolkit.Mvvm.ComponentModel;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>The (still empty) cashier window; the status bar shows who works on which shift and node.</summary>
public sealed class MainViewModel(SessionState session) : ObservableObject
{
    public string CashierName => session.User?.FullName ?? "";

    public string ShiftNumber => session.Shift?.Number ?? "";

    public string NodeName => session.Node is { } node ? $"{node.Code} - {node.Name}" : "";
}
