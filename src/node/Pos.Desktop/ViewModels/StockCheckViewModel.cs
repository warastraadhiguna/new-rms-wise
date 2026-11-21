using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Stock;
using Pos.Shared.Units;

namespace Pos.Desktop.ViewModels;

/// <summary>One expiry date of the product in Form Cek Stok.</summary>
public sealed record StockCheckRow(string LocationCode, DateOnly? ExpiryDate, decimal BaseQty, string StockText, ExpiryState State)
{
    public string ExpiryText => ExpiryDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Tanpa expired";

    /// <summary>"(HAMPIR EXPIRED)" / "(SUDAH EXPIRED)" or empty.</summary>
    public string Marker => ExpiryStatus.Label(State) ?? "";
}

/// <summary>
/// "Form Cek Stok" (RMS 2): the stock of one product per expiry date with "(HAMPIR EXPIRED)" (within
/// stock.expiry_warning_days) and "(SUDAH EXPIRED)" markers. Opened from Form Stok Total (Enter / double-click) or
/// from the menu, where F1 picks the product. F8 = export.
/// </summary>
public sealed partial class StockCheckViewModel : FormTab
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private bool _changingLocation;
    private int _focusSequence;

    public StockCheckViewModel(INodeApi api, IDialogService dialogs)
    {
        _api = api;
        _dialogs = dialogs;
        Locations.Add(StockTotalViewModel.AllLocations);
        _selectedLocation = StockTotalViewModel.AllLocations;
        Initialization = LoadLocationsAsync();
        RequestFocus("rows");
    }

    public override string Title => "Form Cek Stok";

    public Task Initialization { get; }

    /// <summary>The last load (for tests).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<LocationOption> Locations { get; } = [];
    public ObservableCollection<StockCheckRow> Rows { get; } = [];

    public Guid? ProductId { get; private set; }

    [ObservableProperty] private string _productText = "Pilih barang dengan Cari [F1].";
    [ObservableProperty] private LocationOption _selectedLocation;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private FocusRequest? _focusRequest;

    partial void OnSelectedLocationChanged(LocationOption value)
    {
        if (!_changingLocation && ProductId is { } productId)
            PendingLoad = LoadAsync(productId);
    }

    private async Task LoadLocationsAsync()
    {
        try
        {
            foreach (var location in await _api.GetStockLocationsAsync())
                Locations.Add(new LocationOption(location.Id, location.Display));
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>From Form Stok Total: the row's product, filtered to the row's location.</summary>
    public async Task ShowAsync(Guid productId, Guid? locationId)
    {
        await Initialization;
        _changingLocation = true;
        SelectedLocation = Locations.FirstOrDefault(l => l.Id == locationId) ?? StockTotalViewModel.AllLocations;
        _changingLocation = false;
        PendingLoad = LoadAsync(productId);
        await PendingLoad;
    }

    private async Task LoadAsync(Guid productId)
    {
        try
        {
            var stock = await _api.GetStockExpiryAsync(productId, SelectedLocation.Id);
            ProductId = productId;
            ProductText = $"{stock.Sku} - {stock.Name}";
            WarningText = $"Hampir expired = {stock.WarningDays} hari atau kurang (per {stock.Today:dd/MM/yyyy}).";

            Rows.Clear();
            foreach (var row in stock.Rows)
            {
                Rows.Add(new StockCheckRow(row.LocationCode, row.ExpiryDate, row.BaseQty,
                    StockDisplay.Format(row.BaseQty, stock.DisplayUnitCode, stock.DisplayConversion, stock.BaseUnitCode, stock.BaseAllowDecimal),
                    ExpiryStatus.FromCode(row.Status)));
            }
            var total = stock.Rows.Sum(r => r.BaseQty);
            TotalText = "Total: " + StockDisplay.Format(total, stock.DisplayUnitCode, stock.DisplayConversion, stock.BaseUnitCode, stock.BaseAllowDecimal);
            StatusMessage = Rows.Count == 0 ? "Tidak ada stok untuk barang ini." : null;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("rows");
    }

    /// <summary>F1: choose the product with the product search popup.</summary>
    [RelayCommand]
    private async Task PickProductAsync()
    {
        if (_dialogs.PickProduct("") is { } picked)
        {
            PendingLoad = LoadAsync(picked.ProductId);
            await PendingLoad;
        }
    }

    /// <summary>F8 "Export to File".</summary>
    [RelayCommand]
    private void Export()
    {
        if (ProductId is null || Rows.Count == 0)
        {
            StatusMessage = "Belum ada data untuk diexport.";
            return;
        }
        var path = _dialogs.AskSaveFile($"Cek Stok {ProductText.Split(' ')[0]} {DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        if (path is null)
            return;

        try
        {
            XlsxWriter.Write(path, "Cek Stok",
                ["Barang", "Lokasi", "Tanggal Expired", "Stok", "Stok (satuan dasar)", "Keterangan"],
                Rows.Select(r => (IReadOnlyList<object?>)[ProductText, r.LocationCode, r.ExpiryText, r.StockText, r.BaseQty, r.Marker]));
            StatusMessage = $"{Rows.Count.ToString("#,0", Indonesian)} baris disimpan ke {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"File tidak dapat disimpan: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => RaiseCloseRequested();

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
