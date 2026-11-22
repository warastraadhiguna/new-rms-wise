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
    Func<StockCheckViewModel>? StockCheck = null,
    Func<StockCardViewModel>? StockCard = null,
    Func<LocationTransferViewModel>? LocationTransfer = null,
    Func<LocationTransferListViewModel>? LocationTransferList = null,
    Func<ShiftInfo, CloseShiftViewModel>? CloseShift = null);

/// <summary>
/// The main window: a menu per group as in RMS 2 (Penjualan, Stok, Laporan: Kartu Stok), shown by permission (sale.create,
/// stock.view), and every form as a tab with ×. Cashiers start in "Form Penjualan" (F2 reopens it); users without
/// sale.create start in "Form Stok Total". The status bar shows user, shift and node.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly FormFactories _forms;
    private readonly IDialogService? _dialogs;
    private readonly INavigator? _navigator;

    public MainViewModel(SessionState session, Func<SalesViewModel>? salesFactory = null)
        : this(session, new FormFactories(salesFactory))
    {
    }

    public MainViewModel(SessionState session, FormFactories forms, IDialogService? dialogs = null, INavigator? navigator = null)
    {
        _session = session;
        _forms = forms;
        _dialogs = dialogs;
        _navigator = navigator;
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
        else if (CanTransfer)
            OpenLocationTransfer();
    }

    public string CashierName => _session.User?.FullName ?? "";

    public string ShiftNumber => _session.Shift?.Number ?? "";

    public string NodeName => _session.Node is { } node ? $"{node.Code} - {node.Name}" : "";

    /// <summary>The user is a seller (menu "Penjualan" is shown), even when selling is locked on this PC.</summary>
    public bool HasSalesMenu => _session.Has(Permissions.SaleCreate) && _forms.Sales is not null;

    /// <summary>Form Penjualan can be opened: a seller and no other cashier's shift open on this PC.</summary>
    public bool CanSell => HasSalesMenu && _session.ForeignShift is null;

    /// <summary>"Shift milik … sedang terbuka di PC ini" when another cashier's shift locks selling, else null.</summary>
    public string? SalesLockText => _session.ForeignShift is { } shift
        ? $"Shift milik {shift.CashierName ?? "kasir lain"} sedang terbuka di PC ini ({shift.Number})"
        : null;

    public bool IsSalesLocked => SalesLockText is not null;

    /// <summary>Stok Total, Cek Stok and the menu "Laporan".</summary>
    public bool CanViewStock => _session.Has(Permissions.StockView);

    /// <summary>Form Mutasi Lokasi.</summary>
    public bool CanTransfer => _session.Has(Permissions.StockTransfer);

    /// <summary>Menu "Stok" (and Daftar Mutasi Lokasi): stock.view or stock.transfer.</summary>
    public bool ShowStockMenu => CanViewStock || CanTransfer;

    /// <summary>Menu Stok → Form Mutasi Lokasi (one tab).</summary>
    [RelayCommand]
    private void OpenLocationTransfer() => LocationTransferTab();

    private LocationTransferViewModel? LocationTransferTab()
    {
        if (!CanTransfer || _forms.LocationTransfer is null)
            return null;
        var form = Tabs.OfType<LocationTransferViewModel>().FirstOrDefault() ?? _forms.LocationTransfer();
        Show(form);
        return form;
    }

    /// <summary>"Buat Mutasi Balik" from a transfer's detail: Form Mutasi Lokasi filled in the opposite direction.</summary>
    public Task StartTransferReversalAsync(LocationTransfer original) =>
        LocationTransferTab() is { } form ? form.StartReversalAsync(original) : Task.CompletedTask;

    /// <summary>Menu Stok → Daftar Mutasi Lokasi (one tab).</summary>
    [RelayCommand]
    private void OpenLocationTransferList()
    {
        if (!ShowStockMenu || _forms.LocationTransferList is null)
            return;
        if (Tabs.OfType<LocationTransferListViewModel>().FirstOrDefault() is { } open)
        {
            Show(open);
            return;
        }
        var form = _forms.LocationTransferList();
        form.CanReverse = CanTransfer;
        form.ReverseRequested += (_, transfer) => _ = StartTransferReversalAsync(transfer);
        Show(form);
    }

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
        if (SalesForm is { } open)
        {
            Show(open);
            return;
        }
        var form = _forms.Sales!();
        form.CloseShiftRequested += (_, _) => CloseShiftConfirmed();
        Show(form);
    }

    // ---- Tutup Shift ------------------------------------------------------------------------------

    /// <summary>The shift open on this PC: the user's own (or a shared one) or another cashier's that locks selling.</summary>
    private ShiftInfo? ShiftOnThisPc => _session.Shift ?? _session.ForeignShift;

    /// <summary>Menu Penjualan → Tutup Shift: the shift's cashier, or a user with shift.close.</summary>
    public bool CanCloseShift =>
        _forms.CloseShift is not null && ShiftOnThisPc is { } shift
        && (shift.CashierId == _session.User?.Id || _session.Has(Permissions.ShiftClose));

    /// <summary>Menu "Penjualan": sellers, and whoever may close the shift open on this PC.</summary>
    public bool ShowSalesMenu => HasSalesMenu || CanCloseShift;

    /// <summary>Menu Penjualan → Tutup Shift (a deliberate menu choice: no extra confirmation).</summary>
    [RelayCommand]
    private void CloseShift() => CloseShiftConfirmed();

    /// <summary>
    /// Opens the Tutup Shift dialog (refused while Form Penjualan still has a transaction). When the shift was closed
    /// the session ends and the login screen is shown.
    /// </summary>
    private void CloseShiftConfirmed()
    {
        if (!CanCloseShift || _dialogs is null || ShiftOnThisPc is not { } shift)
            return;
        if (SalesForm is { HasItems: true })
        {
            _dialogs.ShowMessage("Masih ada transaksi di Form Penjualan. Selesaikan (Bayar) atau batalkan dulu sebelum tutup shift.");
            return;
        }
        if (!_dialogs.ShowCloseShift(_forms.CloseShift!(shift)))
            return;

        _session.Clear();
        _navigator?.ShowLogin();
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
        form.OpenStockCardRequested += (_, row) => _ = OpenStockCardAsync(row.ProductId, row.LocationId);
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

    /// <summary>Menu Laporan → Kartu Stok (choose the product with F1).</summary>
    [RelayCommand]
    private void OpenStockCard() => StockCardTab();

    /// <summary>F9 from Form Stok Total: the selected row's product and location, this month. One Kartu Stok tab is reused.</summary>
    public Task OpenStockCardAsync(Guid productId, Guid? locationId) =>
        StockCardTab() is { } form ? form.ShowAsync(productId, locationId) : Task.CompletedTask;

    private StockCardViewModel? StockCardTab()
    {
        if (!CanViewStock || _forms.StockCard is null)
            return null;
        var form = Tabs.OfType<StockCardViewModel>().FirstOrDefault();
        if (form is null)
        {
            form = _forms.StockCard();
            form.CanReverse = CanTransfer;
            form.ReverseTransferRequested += (_, transfer) => _ = StartTransferReversalAsync(transfer);
        }
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
