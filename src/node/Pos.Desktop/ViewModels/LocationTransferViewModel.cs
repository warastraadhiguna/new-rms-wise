using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Units;

namespace Pos.Desktop.ViewModels;

/// <summary>One entered line of the transfer, with the FEFO preview from the source location.</summary>
public sealed partial class TransferLineItem(ProductItem item, decimal qty) : ObservableObject
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitCode), nameof(Code), nameof(Name))]
    private ProductItem _item = item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QtyText))]
    private decimal _qty = qty;

    [ObservableProperty] private int _no;
    [ObservableProperty] private string _stockText = "";
    [ObservableProperty] private string _allocationText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _isShort;

    public string Code => Item.Sku;
    public string Name => Item.Name;
    public string UnitCode => Item.UnitCode;
    public string QtyText => Qty.ToString("#,0.####", Indonesian);

    /// <summary>Shows the preview: stock at the source in this line's unit, expiry dates taken FEFO, shortage.</summary>
    public void Apply(LocationTransferPreviewLine preview)
    {
        string Mixed(decimal baseQty) =>
            StockDisplay.Format(baseQty, Item.UnitCode, Item.Conversion, preview.BaseUnitCode, Item.BaseAllowDecimal);

        StockText = Mixed(preview.StockAtSource);
        AllocationText = string.Join("  ·  ", preview.Allocations.Select(a =>
            $"{(a.ExpiryDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Tanpa exp")}: " +
            a.BaseQty.ToString("#,0.####", Indonesian)));
        IsShort = preview.Shortage > 0;
        WarningText = IsShort ? $"Stok asal kurang {preview.Shortage.ToString("#,0.####", Indonesian)} {preview.BaseUnitCode}" : "";
    }
}

/// <summary>
/// "Form Mutasi Lokasi": move stock between two locations of this node (e.g. STORAGE → DISPLAY). Cari with the
/// standard keys and the "x" quantity, F1 search popup, unit button, F8 Atur Jumlah, Shift+Del Hapus. Every change
/// refreshes the FEFO preview per line (stock at the source, expiry dates taken, shortage warning — a shortage is
/// allowed). F6 / Simpan stores it with a client id, so a retry after a network failure is stored once.
/// </summary>
public sealed partial class LocationTransferViewModel : FormTab
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _searchCts;
    private Guid? _pendingId;
    private int _focusSequence;

    public LocationTransferViewModel(INodeApi api, IDialogService dialogs, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _dialogs = dialogs;
        _delay = delay ?? Task.Delay;
        Initialization = LoadLocationsAsync();
        RequestFocus("search");
    }

    public override string Title => "Form Mutasi Lokasi";

    public Task Initialization { get; }

    /// <summary>The running debounced search.</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>The last preview refresh.</summary>
    public Task PendingPreview { get; private set; } = Task.CompletedTask;

    public ObservableCollection<LocationOption> Locations { get; } = [];
    public ObservableCollection<ProductItem> Products { get; } = [];
    public ObservableCollection<TransferLineItem> Lines { get; } = [];

    /// <summary>The id the next save sends (kept after a failure, renewed when the document changes).</summary>
    public Guid? PendingId => _pendingId;

    [ObservableProperty] private LocationOption? _fromLocation;
    [ObservableProperty] private LocationOption? _toLocation;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _qtyText = "1";
    [ObservableProperty] private ProductItem? _selectedProduct;
    [ObservableProperty] private TransferLineItem? _selectedLine;
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string? _warningMessage;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FocusRequest? _focusRequest;

    partial void OnFromLocationChanged(LocationOption? value) => DocumentChanged();
    partial void OnToLocationChanged(LocationOption? value) => DocumentChanged();

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        PendingSearch = SearchAsync(value.Trim(), _searchCts.Token);
    }

    /// <summary>Default direction "Gudang belakang → Etalase": first non-sellable location to the first sellable one.</summary>
    private async Task LoadLocationsAsync()
    {
        try
        {
            var locations = await _api.GetStockLocationsAsync();
            foreach (var location in locations)
                Locations.Add(new LocationOption(location.Id, location.Display));
            var storage = locations.FirstOrDefault(l => !l.IsSellable) ?? locations.FirstOrDefault();
            var display = locations.FirstOrDefault(l => l.IsSellable && l.Id != storage?.Id);
            FromLocation = Locations.FirstOrDefault(l => l.Id == storage?.Id);
            ToLocation = Locations.FirstOrDefault(l => l.Id == display?.Id);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task SearchAsync(string text, CancellationToken token)
    {
        try
        {
            await _delay(SalesViewModel.SearchDelay, token);
            if (token.IsCancellationRequested)
                return;
            var found = text.Length == 0 ? [] : await _api.SearchProductsAsync(text, token);
            if (token.IsCancellationRequested)
                return;
            Products.Clear();
            foreach (var product in found)
                Products.Add(product);
            SelectedProduct = Products.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    // ---- adding lines -----------------------------------------------------------------------------

    /// <summary>Enter in Cari: exact barcode/code wins, otherwise the first result; quantity from the "x" box.</summary>
    [RelayCommand]
    private async Task SearchEnterAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0 || !TryReadQty(out var qty))
            return;
        _searchCts?.Cancel();
        try
        {
            var results = await _api.SearchProductsAsync(text);
            var chosen = results.FirstOrDefault(r => r.IsExactMatch) ?? results.FirstOrDefault();
            if (chosen is null)
            {
                StatusMessage = $"Barang tidak ditemukan: {text}";
                return;
            }
            Add(chosen, qty);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Enter on a search result row (or double-click).</summary>
    [RelayCommand]
    private void AddSelectedProduct()
    {
        if (SelectedProduct is { } product && TryReadQty(out var qty))
            Add(product, qty);
    }

    /// <summary>F1: the large product search popup.</summary>
    [RelayCommand]
    private void OpenProductSearch()
    {
        if (_dialogs.PickProduct(SearchText.Trim()) is { } picked && TryReadQty(out var qty))
            Add(picked, qty);
        else
            RequestFocus("search");
    }

    private void Add(ProductItem product, decimal qty)
    {
        if (!product.AllowDecimal && decimal.Truncate(qty) != qty)
        {
            StatusMessage = $"Jumlah {product.UnitCode} harus bilangan bulat.";
            RequestFocus("qty");
            return;
        }

        var existing = Lines.FirstOrDefault(l => l.Item.ProductUnitId == product.ProductUnitId);
        if (existing is not null)
            existing.Qty += qty;
        else
            Lines.Add(new TransferLineItem(product, qty));
        SelectedLine = existing ?? Lines[^1];
        SearchText = "";
        QtyText = "1";
        StatusMessage = null;
        DocumentChanged();
        RequestFocus("search");
    }

    private bool TryReadQty(out decimal qty)
    {
        if (decimal.TryParse(QtyText.Trim(), NumberStyles.Number, Indonesian, out qty) && qty > 0)
            return true;
        StatusMessage = "Isi jumlah (x) lebih dari 0.";
        RequestFocus("qty");
        return false;
    }

    // ---- editing lines ----------------------------------------------------------------------------

    /// <summary>F8 "Atur Jumlah" for the selected line.</summary>
    [RelayCommand]
    private void ChangeQty()
    {
        if (SelectedLine is not { } line)
            return;
        if (_dialogs.AskQuantity(line.Name, line.Qty) is { } qty && qty > 0)
        {
            if (!line.Item.AllowDecimal && decimal.Truncate(qty) != qty)
                StatusMessage = $"Jumlah {line.UnitCode} harus bilangan bulat.";
            else
            {
                line.Qty = qty;
                DocumentChanged();
            }
        }
        RequestFocus("search");
    }

    /// <summary>Shift+Del "Hapus": removes the selected line (no supervisor needed: nothing is sold).</summary>
    [RelayCommand]
    private void DeleteLine()
    {
        if (SelectedLine is not { } line)
            return;
        var index = Lines.IndexOf(line);
        Lines.Remove(line);
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
        DocumentChanged();
        RequestFocus("search");
    }

    /// <summary>The unit button of a line: another unit of the same product.</summary>
    [RelayCommand]
    private async Task ChangeUnitAsync(TransferLineItem? line)
    {
        line ??= SelectedLine;
        if (line is null)
            return;
        try
        {
            var units = await _api.GetProductUnitsAsync(line.Item.ProductId);
            if (_dialogs.PickUnit(line.Name, units, line.Item.ProductUnitId) is { } unit && unit.ProductUnitId != line.Item.ProductUnitId)
            {
                line.Item = unit;
                DocumentChanged();
            }
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("search");
    }

    // ---- preview and save -------------------------------------------------------------------------

    /// <summary>Lines, locations or units changed: renumber, new document id next time, fresh preview.</summary>
    private void DocumentChanged()
    {
        _pendingId = null;
        for (var i = 0; i < Lines.Count; i++)
            Lines[i].No = i + 1;
        PendingPreview = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        WarningMessage = null;
        if (FromLocation?.Id is not { } from || Lines.Count == 0)
            return;
        var snapshot = Lines.ToList();
        try
        {
            var preview = await _api.PreviewLocationTransferAsync(from, Requests(snapshot));
            if (!snapshot.SequenceEqual(Lines))
                return;                                                     // changed meanwhile; a newer preview follows
            foreach (var (line, result) in snapshot.Zip(preview.Lines))
                line.Apply(result);
            var shortCount = snapshot.Count(l => l.IsShort);
            WarningMessage = shortCount == 0 ? null
                : $"Stok di lokasi asal tidak cukup untuk {shortCount} barang. Tetap boleh disimpan; stok asal menjadi minus.";
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static List<LocationTransferLineRequest> Requests(IEnumerable<TransferLineItem> lines) =>
        lines.Select(l => new LocationTransferLineRequest(l.Item.ProductUnitId, l.Qty)).ToList();

    /// <summary>F6 / Simpan.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
            return;
        if (FromLocation?.Id is not { } from || ToLocation?.Id is not { } to)
        {
            StatusMessage = "Pilih Lokasi Asal dan Lokasi Tujuan.";
            return;
        }
        if (from == to)
        {
            StatusMessage = "Lokasi asal dan tujuan tidak boleh sama.";
            return;
        }
        if (Lines.Count == 0)
        {
            StatusMessage = "Belum ada barang yang dimutasi.";
            RequestFocus("search");
            return;
        }

        _pendingId ??= Guid.CreateVersion7();
        IsBusy = true;
        try
        {
            var saved = await _api.CreateLocationTransferAsync(new CreateLocationTransferRequest(
                _pendingId.Value, from, to, Requests(Lines), string.IsNullOrWhiteSpace(NotesText) ? null : NotesText.Trim()));
            Lines.Clear();
            Products.Clear();
            NotesText = "";
            WarningMessage = null;
            _pendingId = null;
            StatusMessage = $"Mutasi {saved.Number} tersimpan ({saved.FromLocationCode} → {saved.ToLocationCode}, {saved.Lines.Count} barang).";
        }
        catch (NodeApiException ex) when (ex.Code is "node_unreachable")
        {
            StatusMessage = $"{ex.Message} Tekan F6 lagi untuk mengirim ulang mutasi yang sama (tidak akan tercatat dua kali).";
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        RequestFocus("search");
    }

    /// <summary>Esc: closes; with lines it asks first.</summary>
    [RelayCommand]
    private void Close()
    {
        if (Lines.Count > 0 && !_dialogs.Confirm("Mutasi belum disimpan. Tutup dan buang daftar barang ini?"))
        {
            RequestFocus("search");
            return;
        }
        RaiseCloseRequested();
    }

    public override void RequestClose() => CloseCommand.Execute(null);

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
