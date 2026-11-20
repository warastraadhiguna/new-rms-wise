using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Auth;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>A cart row as shown in the "Transaksi" grid (No, Kode Barang, Nama Barang, QTY, Satuan, Harga, Sub Total).</summary>
public sealed record CartLineRow(
    int Index, int No, string Code, string Name, decimal Qty, string UnitCode, decimal Price, decimal SubTotal,
    Guid ProductId, Guid ProductUnitId, bool AllowQtyMultiplier);

/// <summary>Which input should get keyboard focus; the sequence makes repeated requests distinct.</summary>
public sealed record FocusRequest(string Target, int Sequence);

/// <summary>
/// "Form Penjualan" as in RMS 2: member, product search with x-qty, product grid, cart with total,
/// F1 search popup, * multiplier, F8 qty, unit change, Shift+Del delete (supervisor if needed),
/// Ctrl+Shift+O unlock delete, F12 cancel with reason, Esc close. Payment (F6) comes in 7b.
/// The cart is saved locally after every change and restored after a crash.
/// </summary>
public sealed partial class SalesViewModel : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly SessionState _session;
    private readonly IDialogService _dialogs;
    private readonly ICartStore _cartStore;
    private readonly IViewSettingsStore _viewStore;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Cart _cart = new();
    private CancellationTokenSource? _searchCts;
    private bool _suppressSearch;
    private bool _suppressCustomerText;
    private bool _loadingSettings;
    private int _focusSequence;
    private int? _multiplierIndex;

    public SalesViewModel(
        INodeApi api, SessionState session, IDialogService dialogs, ICartStore cartStore, IViewSettingsStore viewStore,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _session = session;
        _dialogs = dialogs;
        _cartStore = cartStore;
        _viewStore = viewStore;
        _delay = delay ?? Task.Delay;

        LoadViewSettings();
        RestoreCart();
        RequestFocus("search");
    }

    /// <summary>Raised when the form should close (Esc).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The running debounced search (for tests and the UI).</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public Cart Cart => _cart;

    public ObservableCollection<ProductItem> Products { get; } = [];

    public ObservableCollection<CartLineRow> Lines { get; } = [];

    [ObservableProperty] private string _customerText = "";
    [ObservableProperty] private CustomerItem? _customer;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _qtyText = "1";
    [ObservableProperty] private bool _isMultiplierMode;
    [ObservableProperty] private ProductItem? _selectedProduct;
    [ObservableProperty] private CartLineRow? _selectedLine;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _warningMessage;
    [ObservableProperty] private bool _isDeleteUnlocked;
    [ObservableProperty] private FocusRequest? _focusRequest;

    [ObservableProperty] private bool _showBrand;
    [ObservableProperty] private bool _showShelf;
    [ObservableProperty] private bool _showStock;
    [ObservableProperty] private bool _showCategory;
    [ObservableProperty] private bool _showDiscountPrice;
    [ObservableProperty] private bool _showUnit = true;

    public bool HasItems => !_cart.IsEmpty;

    // ---- search ---------------------------------------------------------------------------------

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch)
            return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        PendingSearch = DebouncedSearchAsync(value.Trim(), _searchCts.Token);
    }

    private async Task DebouncedSearchAsync(string text, CancellationToken token)
    {
        if (text.Length == 0)
        {
            Products.Clear();
            return;
        }

        try
        {
            await _delay(SearchDelay, token);
            if (token.IsCancellationRequested)
                return;
            var results = await _api.SearchProductsAsync(text, token);
            if (token.IsCancellationRequested)
                return;
            ShowProducts(results);
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void ShowProducts(IReadOnlyList<ProductItem> results)
    {
        Products.Clear();
        foreach (var item in results)
            Products.Add(item);
        SelectedProduct = Products.FirstOrDefault();
    }

    /// <summary>Enter in "Cari": exact barcode/code wins, otherwise the first row; qty from the "x" box.</summary>
    [RelayCommand]
    private async Task SearchEnterAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
            return;
        if (!TryReadQty(out var qty))
            return;

        _searchCts?.Cancel();
        IReadOnlyList<ProductItem> results;
        try
        {
            results = await _api.SearchProductsAsync(text);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        var chosen = results.FirstOrDefault(r => r.IsExactMatch) ?? results.FirstOrDefault();
        if (chosen is null)
        {
            StatusMessage = $"Barang tidak ditemukan: {text}";
            return;
        }

        ShowProducts(results);
        AddToCart(chosen, qty);
    }

    /// <summary>Enter in the "x" box: applies the multiplier, or adds the search text with this qty.</summary>
    [RelayCommand]
    private async Task QtyEnterAsync()
    {
        if (IsMultiplierMode)
        {
            ApplyMultiplier();
            return;
        }
        await SearchEnterAsync();
    }

    [RelayCommand]
    private Task AddSelectedProductAsync()
    {
        if (SelectedProduct is { } item && TryReadQty(out var qty))
            AddToCart(item, qty);
        return Task.CompletedTask;
    }

    /// <summary>F1: large search popup.</summary>
    [RelayCommand]
    private void OpenProductSearch()
    {
        var picked = _dialogs.PickProduct(SearchText.Trim());
        if (picked is not null && TryReadQty(out var qty))
            AddToCart(picked, qty);
        else
            RequestFocus("search");
    }

    private bool TryReadQty(out decimal qty)
    {
        if (MoneyInput.TryParse(QtyText, out qty) && qty > 0)
            return true;
        StatusMessage = "Jumlah di kotak x tidak valid.";
        RequestFocus("qty");
        return false;
    }

    private void AddToCart(ProductItem item, decimal qty)
    {
        try
        {
            var index = _cart.Add(item.ToCartItem(), qty);
            StatusMessage = null;
            CartChanged(index);
        }
        catch (CartException ex)
        {
            StatusMessage = CartMessage(ex, item.Name, item.UnitCode);
        }

        _suppressSearch = true;
        SearchText = "";
        _suppressSearch = false;
        QtyText = "1";
        RequestFocus("search");
    }

    // ---- multiplier, qty, unit --------------------------------------------------------------------

    /// <summary>"*": the next number typed becomes the qty of the selected (or last) line.</summary>
    [RelayCommand]
    private void StartMultiplier()
    {
        var index = TargetIndex();
        if (index is null)
        {
            StatusMessage = "Keranjang masih kosong.";
            return;
        }

        var line = _cart.Lines[index.Value];
        if (!line.Item.AllowQtyMultiplier)
        {
            StatusMessage = $"{line.Item.Name} harus discan satu per satu (tidak boleh memakai pengali).";
            return;
        }

        _multiplierIndex = index;
        IsMultiplierMode = true;
        QtyText = "";
        StatusMessage = $"Ketik jumlah untuk {line.Item.Name}, lalu Enter.";
        RequestFocus("qty");
    }

    private void ApplyMultiplier()
    {
        if (_multiplierIndex is not { } index || index >= _cart.Lines.Count)
        {
            EndMultiplier();
            return;
        }
        if (!MoneyInput.TryParse(QtyText, out var qty) || qty <= 0)
        {
            StatusMessage = "Jumlah tidak valid.";
            return;
        }

        var item = _cart.Lines[index].Item;
        try
        {
            _cart.SetQty(index, qty);
            StatusMessage = null;
            CartChanged(index);
        }
        catch (CartException ex)
        {
            StatusMessage = CartMessage(ex, item.Name, item.UnitCode);
        }
        EndMultiplier();
    }

    private void EndMultiplier()
    {
        IsMultiplierMode = false;
        _multiplierIndex = null;
        QtyText = "1";
        RequestFocus("search");
    }

    /// <summary>F8 "Atur Jumlah".</summary>
    [RelayCommand]
    private void ChangeQty()
    {
        if (TargetIndex() is not { } index)
        {
            StatusMessage = "Keranjang masih kosong.";
            return;
        }

        var line = _cart.Lines[index];
        if (_dialogs.AskQuantity(line.Item.Name, line.Qty) is { } qty)
        {
            try
            {
                _cart.SetQty(index, qty);
                StatusMessage = null;
                CartChanged(index);
            }
            catch (CartException ex)
            {
                StatusMessage = CartMessage(ex, line.Item.Name, line.Item.UnitCode);
            }
        }
        RequestFocus("search");
    }

    /// <summary>Button in the "Satuan" cell: switch to another unit of the same product.</summary>
    [RelayCommand]
    private async Task ChangeUnitAsync(CartLineRow? row)
    {
        row ??= SelectedLine;
        if (row is null)
            return;

        try
        {
            var units = await _api.GetProductUnitsAsync(row.ProductId);
            var picked = _dialogs.PickUnit(row.Name, units, row.ProductUnitId);
            if (picked is not null && picked.ProductUnitId != row.ProductUnitId)
            {
                var index = _cart.ChangeUnit(row.Index, picked.ToCartItem());
                StatusMessage = null;
                CartChanged(index);
            }
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (CartException ex)
        {
            StatusMessage = CartMessage(ex, row.Name, row.UnitCode);
        }
        RequestFocus("search");
    }

    // ---- delete -----------------------------------------------------------------------------------

    private bool CanDeleteWithoutSupervisor =>
        IsDeleteUnlocked || _session.Permissions.Contains(Permissions.SaleLineDelete) || _session.Permissions.Contains(Permissions.All);

    /// <summary>Shift+Del "Hapus": delete the selected (or last) line; supervisor password when not allowed.</summary>
    [RelayCommand]
    private async Task DeleteLineAsync()
    {
        if (TargetIndex() is not { } index)
        {
            StatusMessage = "Keranjang masih kosong.";
            return;
        }

        if (!CanDeleteWithoutSupervisor && !await ApproveDeleteAsync("Hapus barang dari keranjang"))
        {
            RequestFocus("search");
            return;
        }

        _cart.RemoveAt(index);
        StatusMessage = null;
        CartChanged(_cart.IsEmpty ? null : Math.Min(index, _cart.Lines.Count - 1));
        RequestFocus("search");
    }

    /// <summary>Ctrl+Shift+O: a supervisor opens deleting for the rest of this transaction (as in RMS 2).</summary>
    [RelayCommand]
    private async Task UnlockDeleteAsync()
    {
        if (await ApproveDeleteAsync("Buka izin hapus untuk transaksi ini"))
        {
            IsDeleteUnlocked = true;
            StatusMessage = "Izin hapus dibuka untuk transaksi ini.";
        }
        RequestFocus("search");
    }

    private async Task<bool> ApproveDeleteAsync(string purpose)
    {
        if (_dialogs.AskSupervisor(purpose) is not { } credentials)
            return false;

        try
        {
            await _api.ApproveAsync(Permissions.SaleLineDelete, credentials.Username, credentials.Password);
            return true;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    // ---- pay --------------------------------------------------------------------------------------

    /// <summary>Id for the next payment of this exact cart; reused after a failed or interrupted payment.</summary>
    private Guid? _pendingSaleId;

    /// <summary>F6 "Bayar": payment dialog; on success show the nota and change, clear the cart.</summary>
    [RelayCommand]
    private async Task PayAsync()
    {
        if (_cart.IsEmpty)
        {
            StatusMessage = "Keranjang masih kosong.";
            RequestFocus("search");
            return;
        }

        SalesConfig config;
        try
        {
            config = await _api.GetSalesConfigAsync();
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        if (_pendingSaleId is null)
        {
            _pendingSaleId = Guid.CreateVersion7();
            SaveCart();
        }

        var payment = new PaymentViewModel(_api, _dialogs, config, _cart, Customer, _pendingSaleId.Value);
        var result = _dialogs.ShowPayment(payment);
        if (result is null)
        {
            RequestFocus("search");
            return;
        }

        ClearTransaction();
        _dialogs.ShowSaleResult(result);
        StatusMessage = $"Nota {result.Sale.Number} tersimpan. Kembali Rp {result.Sale.Change.ToString("#,0", Indonesian)}.";
        WarningMessage = result.Warnings.Count == 0 ? null : string.Join(" ", result.Warnings.Select(SaleWarningText));
        RequestFocus("search");
    }

    private static string SaleWarningText(SaleWarningInfo warning) => warning.Code switch
    {
        "credit_limit_exceeded" => "Kredit pelanggan melebihi limit.",
        _ => warning.Message,
    };

    // ---- cancel and close ------------------------------------------------------------------------

    /// <summary>F12 "Batal": cancel the whole cart with a reason (saved as a cart_cancellations document).</summary>
    [RelayCommand]
    private async Task CancelTransactionAsync()
    {
        if (_cart.IsEmpty)
        {
            StatusMessage = "Keranjang masih kosong.";
            RequestFocus("search");
            return;
        }

        var reason = _dialogs.AskCancelReason();
        if (string.IsNullOrWhiteSpace(reason))
        {
            RequestFocus("search");
            return;
        }

        var lines = _cart.Lines.Select(l => new CartCancellationLine(l.Item.ProductUnitId, l.Qty, l.Price, l.SubTotal)).ToList();
        try
        {
            var result = await _api.CancelCartAsync(Guid.CreateVersion7(), reason.Trim(), _cart.Total, lines);
            ClearTransaction();
            StatusMessage = $"Transaksi dibatalkan ({result.Number}).";
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("search");
    }

    /// <summary>Esc: leave the multiplier, or close the form (confirm when the cart is not empty).</summary>
    [RelayCommand]
    private void Escape()
    {
        if (IsMultiplierMode)
        {
            EndMultiplier();
            StatusMessage = null;
            return;
        }

        if (!_cart.IsEmpty && !_dialogs.Confirm("Keranjang belum dibayar. Tutup Form Penjualan dan kosongkan keranjang?"))
        {
            RequestFocus("search");
            return;
        }

        ClearTransaction();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Empties the cart and resets everything that belongs to one transaction.</summary>
    public void ClearTransaction()
    {
        _cart.Clear();
        _pendingSaleId = null;
        IsDeleteUnlocked = false;
        Customer = null;
        WarningMessage = null;
        CartChanged(null);
        _cartStore.Clear();
    }

    // ---- member -----------------------------------------------------------------------------------

    partial void OnCustomerTextChanged(string value)
    {
        if (_suppressCustomerText)
            return;
        if (Customer is not null && value != Customer.Display)
            Customer = null;
    }

    partial void OnCustomerChanged(CustomerItem? value)
    {
        if (!_restoring)
            _pendingSaleId = null;   // another customer = another sale
        _suppressCustomerText = true;
        CustomerText = value?.Display ?? (string.IsNullOrWhiteSpace(CustomerText) ? "" : CustomerText);
        _suppressCustomerText = false;
        SaveCart();
    }

    /// <summary>Enter in "Member": one match is taken, several open the picker.</summary>
    [RelayCommand]
    private async Task MemberEnterAsync()
    {
        var text = CustomerText.Trim();
        if (text.Length == 0)
        {
            Customer = null;
            return;
        }

        try
        {
            var found = await _api.SearchCustomersAsync(text);
            Customer = found.Count == 1 ? found[0] : _dialogs.PickCustomer(text) ?? Customer;
            if (Customer is null)
                StatusMessage = found.Count == 0 ? $"Member tidak ditemukan: {text}" : null;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("search");
    }

    /// <summary>The "..." button next to Member.</summary>
    [RelayCommand]
    private void PickCustomer()
    {
        if (_dialogs.PickCustomer(Customer is null ? CustomerText.Trim() : "") is { } picked)
            Customer = picked;
        RequestFocus("search");
    }

    // ---- view settings ------------------------------------------------------------------------------

    partial void OnShowBrandChanged(bool value) => SaveViewSettings();
    partial void OnShowShelfChanged(bool value) => SaveViewSettings();
    partial void OnShowStockChanged(bool value) => SaveViewSettings();
    partial void OnShowCategoryChanged(bool value) => SaveViewSettings();
    partial void OnShowDiscountPriceChanged(bool value) => SaveViewSettings();
    partial void OnShowUnitChanged(bool value) => SaveViewSettings();

    private void LoadViewSettings()
    {
        _loadingSettings = true;
        var s = _viewStore.Load();
        ShowBrand = s.ShowBrand;
        ShowShelf = s.ShowShelf;
        ShowStock = s.ShowStock;
        ShowCategory = s.ShowCategory;
        ShowDiscountPrice = s.ShowDiscountPrice;
        ShowUnit = s.ShowUnit;
        _loadingSettings = false;
    }

    private void SaveViewSettings()
    {
        if (!_loadingSettings)
            _viewStore.Save(new SalesViewSettings(ShowBrand, ShowShelf, ShowStock, ShowCategory, ShowDiscountPrice, ShowUnit));
    }

    // ---- cart bookkeeping -------------------------------------------------------------------------

    private int? TargetIndex() =>
        SelectedLine is { } row && row.Index < _cart.Lines.Count ? row.Index
        : _cart.IsEmpty ? null
        : _cart.Lines.Count - 1;

    private void CartChanged(int? select)
    {
        if (!_restoring)
            _pendingSaleId = null;   // a changed cart is a different sale
        Lines.Clear();
        for (var i = 0; i < _cart.Lines.Count; i++)
        {
            var l = _cart.Lines[i];
            Lines.Add(new CartLineRow(i, i + 1, l.Item.Code, l.Item.Name, l.Qty, l.Item.UnitCode, l.Price, l.SubTotal,
                l.Item.ProductId, l.Item.ProductUnitId, l.Item.AllowQtyMultiplier));
        }
        SelectedLine = select is { } s && s < Lines.Count ? Lines[s] : null;
        Total = _cart.Total;
        OnPropertyChanged(nameof(HasItems));

        WarningMessage = _cart.StockWarnings() is { Count: > 0 } warnings
            ? string.Join(" ", warnings.Select(w =>
                $"Stok {w.Name} di lokasi jual tinggal {Qty(w.AvailableBaseQty)}, di keranjang {Qty(w.InCartBaseQty)}. Transaksi tetap bisa dilanjutkan."))
            : null;
        SaveCart();
    }

    private void SaveCart()
    {
        if (_cart.IsEmpty && Customer is null)
            _cartStore.Clear();
        else
            _cartStore.Save(new CartSnapshot(
                _cart.Lines.Select(l => new CartSnapshotLine(l.Item, l.Qty)).ToList(), Customer, _pendingSaleId));
    }

    private bool _restoring;

    private void RestoreCart()
    {
        if (_cartStore.Load() is not { } snapshot || snapshot.Lines.Count == 0)
            return;

        _restoring = true;
        try
        {
            _pendingSaleId = snapshot.PendingSaleId;
            _cart.Restore(snapshot.Lines.Select(l => new CartLine(l.Item, l.Qty)));
            Customer = snapshot.Customer;
            CartChanged(_cart.Lines.Count - 1);
        }
        finally
        {
            _restoring = false;
        }
        StatusMessage = "Keranjang sebelumnya dipulihkan.";
    }

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);

    private static string Qty(decimal value) => value.ToString("#,0.####", Indonesian);

    private static string CartMessage(CartException ex, string name, string unit) => ex.Code switch
    {
        "invalid_quantity" => "Jumlah harus lebih dari 0.",
        "decimal_not_allowed" => $"Satuan {unit} harus jumlah bulat.",
        "qty_multiplier_not_allowed" => $"{name} harus discan satu per satu (tidak boleh memakai pengali).",
        "no_price" => $"{name} belum punya harga untuk jumlah ini.",
        "different_product" => "Satuan yang dipilih bukan milik barang ini.",
        _ => ex.Message,
    };
}
