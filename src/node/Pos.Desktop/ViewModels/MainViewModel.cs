using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Auth;

namespace Pos.Desktop.ViewModels;

/// <summary>Creates the forms the main window can open (null = not available in this build/test).</summary>
public sealed record FormFactories(
    Func<SalesViewModel>? Sales = null,
    Func<StockTotalViewModel>? StockTotal = null,
    Func<StockCheckViewModel>? StockCheck = null);

/// <summary>
/// The main window: a menu per group as in RMS 2 (Penjualan, Stok, Laporan), shown by permission (sale.create,
/// stock.view), and every form as a tab with ×. Cashiers start in "Form Penjualan" (F2 reopens it); users without
/// sale.create start in "Form Stok Total". The status bar shows user, shift and node.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly FormFactories _forms;

    public MainViewModel(SessionState session, Func<SalesViewModel>? salesFactory = null)
        : this(session, new FormFactories(salesFactory))
    {
    }

    public MainViewModel(SessionState session, FormFactories forms)
    {
        _session = session;
        _forms = forms;
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SalesForm));
            OnPropertyChanged(nameof(IsSalesFormOpen));
            OnPropertyChanged(nameof(HasTabs));
        };

        if (CanSell)
            OpenSalesForm();
        else if (CanViewStock)
            OpenStockTotal();
    }

    public string CashierName => _session.User?.FullName ?? "";

    public string ShiftNumber => _session.Shift?.Number ?? "";

    public string NodeName => _session.Node is { } node ? $"{node.Code} - {node.Name}" : "";

    /// <summary>Menu "Penjualan".</summary>
    public bool CanSell => _session.Has(Permissions.SaleCreate) && _forms.Sales is not null;

    /// <summary>Menus "Stok" and "Laporan".</summary>
    public bool CanViewStock => _session.Has(Permissions.StockView);

    public ObservableCollection<FormTab> Tabs { get; } = [];

    [ObservableProperty] private FormTab? _selectedTab;

    public bool HasTabs => Tabs.Count > 0;

    public SalesViewModel? SalesForm => Tabs.OfType<SalesViewModel>().FirstOrDefault();

    public bool IsSalesFormOpen => SalesForm is not null;

    /// <summary>F2 "Form Penjualan".</summary>
    [RelayCommand]
    private void OpenSalesForm()
    {
        if (!CanSell)
            return;
        Show(SalesForm ?? _forms.Sales!());
    }

    /// <summary>Menu Stok → Stok Total.</summary>
    [RelayCommand]
    private void OpenStockTotal()
    {
        if (!CanViewStock || _forms.StockTotal is null)
            return;
        if (Tabs.OfType<StockTotalViewModel>().FirstOrDefault() is { } open)
        {
            Show(open);
            return;
        }

        var form = _forms.StockTotal();
        form.OpenStockCheckRequested += (_, row) => _ = OpenStockCheckAsync(row.ProductId, row.LocationId);
        Show(form);
    }

    /// <summary>Menu Stok → Cek Stok (choose the product with F1).</summary>
    [RelayCommand]
    private void OpenStockCheck() => StockCheckTab();

    /// <summary>From Form Stok Total: the selected row's product and location. One Cek Stok tab is reused.</summary>
    public Task OpenStockCheckAsync(Guid productId, Guid? locationId) =>
        StockCheckTab() is { } form ? form.ShowAsync(productId, locationId) : Task.CompletedTask;

    private StockCheckViewModel? StockCheckTab()
    {
        if (!CanViewStock || _forms.StockCheck is null)
            return null;
        var form = Tabs.OfType<StockCheckViewModel>().FirstOrDefault() ?? _forms.StockCheck();
        Show(form);
        return form;
    }

    /// <summary>The tab's ×: the form decides (the sales form asks when its cart is not empty).</summary>
    [RelayCommand]
    private void CloseTab(FormTab? tab) => tab?.RequestClose();

    /// <summary>Kept for the sales form's tab (same as its ×).</summary>
    [RelayCommand]
    private void CloseSalesForm() => SalesForm?.RequestClose();

    private void Show(FormTab tab)
    {
        if (!Tabs.Contains(tab))
        {
            tab.CloseRequested += OnTabCloseRequested;
            Tabs.Add(tab);
        }
        SelectedTab = tab;
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not FormTab tab)
            return;
        tab.CloseRequested -= OnTabCloseRequested;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
    }
}
