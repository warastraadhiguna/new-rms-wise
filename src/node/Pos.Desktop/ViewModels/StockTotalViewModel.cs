using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>A location in the filter; <c>Id</c> null = "Semua Lokasi".</summary>
public sealed record LocationOption(Guid? Id, string Display);

/// <summary>
/// "Form Stok Total" (RMS 2): one row per product per location with Kode Barang, Nama Barang, Jenis Barang, Merek,
/// Letak Barang, Lokasi, Satuan, Stok (mixed units). Cari (SearchNavigation keys), location filter, Atur Tampilan,
/// Enter / double-click = Form Cek Stok, F8 = export to Excel. No purchase price or cost at a node.
/// </summary>
public sealed partial class StockTotalViewModel : FormTab
{
    public const int PageSize = 500;
    public const int ExportPageSize = 1000;
    public static readonly TimeSpan SearchDelay = SalesViewModel.SearchDelay;

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly IPreferenceStore<StockTotalViewSettings> _viewStore;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _searchCts;
    private bool _loadingSettings;
    private int _focusSequence;

    public StockTotalViewModel(
        INodeApi api, IDialogService dialogs, IPreferenceStore<StockTotalViewSettings> viewStore,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _dialogs = dialogs;
        _viewStore = viewStore;
        _delay = delay ?? Task.Delay;

        _loadingSettings = true;
        var settings = viewStore.Load();
        ShowCategory = settings.ShowCategory;
        ShowBrand = settings.ShowBrand;
        ShowShelf = settings.ShowShelf;
        ShowUnit = settings.ShowUnit;
        _loadingSettings = false;

        Locations.Add(AllLocations);
        _selectedLocation = AllLocations;
        RequestFocus("search");
        Initialization = InitializeAsync();
    }

    public static readonly LocationOption AllLocations = new(null, "Semua Lokasi");

    public override string Title => "Form Stok Total";

    /// <summary>Locations and the first page (for tests and the UI).</summary>
    public Task Initialization { get; }

    /// <summary>The running debounced reload.</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public ObservableCollection<LocationOption> Locations { get; } = [];
    public ObservableCollection<StockTotalRow> Rows { get; } = [];

    /// <summary>Enter / double-click: open "Form Cek Stok" for the row.</summary>
    public event EventHandler<StockTotalRow>? OpenStockCheckRequested;

    /// <summary>F9: open the Kartu Stok of the row's product and location.</summary>
    public event EventHandler<StockTotalRow>? OpenStockCardRequested;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private LocationOption _selectedLocation;
    [ObservableProperty] private StockTotalRow? _selectedRow;
    [ObservableProperty] private int _total;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FocusRequest? _focusRequest;

    [ObservableProperty] private bool _showCategory;
    [ObservableProperty] private bool _showBrand;
    [ObservableProperty] private bool _showShelf;
    [ObservableProperty] private bool _showUnit;

    /// <summary>"Menampilkan 500 dari 1.234 baris".</summary>
    public string CountText => Rows.Count == Total
        ? $"{Total.ToString("#,0", Indonesian)} baris"
        : $"Menampilkan {Rows.Count.ToString("#,0", Indonesian)} dari {Total.ToString("#,0", Indonesian)} baris — persempit pencarian.";

    partial void OnSearchTextChanged(string value) => ScheduleReload(debounce: true);

    partial void OnSelectedLocationChanged(LocationOption value) => ScheduleReload(debounce: false);

    partial void OnShowCategoryChanged(bool value) => SaveViewSettings();
    partial void OnShowBrandChanged(bool value) => SaveViewSettings();
    partial void OnShowShelfChanged(bool value) => SaveViewSettings();
    partial void OnShowUnitChanged(bool value) => SaveViewSettings();

    private async Task InitializeAsync()
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
        ScheduleReload(debounce: false);
        await PendingSearch;
    }

    private void ScheduleReload(bool debounce)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        PendingSearch = ReloadAsync(debounce, _searchCts.Token);
    }

    private async Task ReloadAsync(bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce)
                await _delay(SearchDelay, token);
            if (token.IsCancellationRequested)
                return;
            IsBusy = true;
            var page = await _api.GetStockTotalsAsync(SearchText.Trim(), SelectedLocation.Id, 0, PageSize, token);
            if (token.IsCancellationRequested)
                return;

            Rows.Clear();
            foreach (var row in page.Items)
                Rows.Add(row);
            Total = page.Total;
            SelectedRow = Rows.FirstOrDefault();
            StatusMessage = page.Total == 0 && SearchText.Trim().Length > 0 ? "Barang tidak ditemukan." : null;
            OnPropertyChanged(nameof(CountText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Enter in the grid or double-click.</summary>
    [RelayCommand]
    private void OpenStockCheck()
    {
        if (SelectedRow is { } row)
            OpenStockCheckRequested?.Invoke(this, row);
    }

    /// <summary>F9 "Kartu Stok".</summary>
    [RelayCommand]
    private void OpenStockCard()
    {
        if (SelectedRow is { } row)
            OpenStockCardRequested?.Invoke(this, row);
    }

    /// <summary>F8 "Export to File": every row of the current search and location, not only the page shown.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = _dialogs.AskSaveFile($"Stok Total {DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        if (path is null)
            return;

        try
        {
            var rows = new List<StockTotalRow>();
            for (var offset = 0; ; offset += ExportPageSize)
            {
                var page = await _api.GetStockTotalsAsync(SearchText.Trim(), SelectedLocation.Id, offset, ExportPageSize);
                rows.AddRange(page.Items);
                if (page.Items.Count == 0 || rows.Count >= page.Total)
                    break;
            }

            XlsxWriter.Write(path, "Stok Total",
                ["Kode Barang", "Nama Barang", "Jenis Barang", "Merek", "Letak Barang", "Lokasi", "Satuan", "Stok", "Stok (satuan dasar)"],
                rows.Select(r => (IReadOnlyList<object?>)
                    [r.Sku, r.Name, r.Category, r.Brand, r.ShelfLocation, r.LocationCode, r.BaseUnitCode, r.StockText, r.BaseQty]));
            StatusMessage = $"{rows.Count.ToString("#,0", Indonesian)} baris disimpan ke {path}.";
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"File tidak dapat disimpan: {ex.Message}";
        }
        RequestFocus("search");
    }

    /// <summary>Esc closes the form.</summary>
    [RelayCommand]
    private void Close() => RaiseCloseRequested();

    private void SaveViewSettings()
    {
        if (!_loadingSettings)
            _viewStore.Save(new StockTotalViewSettings(ShowCategory, ShowBrand, ShowShelf, ShowUnit));
    }

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
