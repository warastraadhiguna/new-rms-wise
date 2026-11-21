using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Printing;
using Pos.Shared.Stock;

namespace Pos.Desktop.ViewModels;

/// <summary>One line of the Kartu Stok grid; the first line is the opening balance ("Saldo Awal").</summary>
public sealed record StockCardLine(
    bool IsOpening,
    DateTime? LocalTime,
    string DocType,
    string DocLabel,
    Guid? DocId,
    string? DocNumber,
    string? UserName,
    string? LocationCode,
    DateOnly? ExpiryDate,
    decimal QtyIn,
    decimal QtyOut,
    decimal Balance)
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public string TimeText => LocalTime is { } t ? t.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "";
    public string ExpiryText => ExpiryDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
    public string InText => QtyIn == 0 ? "" : QtyIn.ToString("#,0.####", Indonesian);
    public string OutText => QtyOut == 0 ? "" : QtyOut.ToString("#,0.####", Indonesian);
    public string BalanceText => Balance.ToString("#,0.####", Indonesian);
}

/// <summary>
/// "Kartu Stok" (new, not in RMS 2): one product, one location or all, branch dates Dari..Sampai. First line =
/// opening balance; then every stock movement with Tanggal-jam (branch time), Jenis Dokumen, No Dokumen, Oleh,
/// Lokasi, Tanggal Expired, Masuk, Keluar and running Saldo; the last balance equals the current stock. Loaded in
/// pages of 500 ("Tampilkan berikutnya"); F8 exports every row; double-click shows the source document.
/// No purchase price or cost at a node.
/// </summary>
public sealed partial class StockCardViewModel : FormTab
{
    public const int PageSize = 500;
    public const int ExportPageSize = 1000;

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private StockCard? _card;
    private int _loaded;
    private int _focusSequence;

    public StockCardViewModel(INodeApi api, IDialogService dialogs, Func<DateOnly>? today = null)
    {
        _api = api;
        _dialogs = dialogs;
        var now = (today ?? (() => DateOnly.FromDateTime(DateTime.Now)))();
        _fromDate = new DateTime(now.Year, now.Month, 1);
        _toDate = now.ToDateTime(TimeOnly.MinValue);
        Locations.Add(StockTotalViewModel.AllLocations);
        _selectedLocation = StockTotalViewModel.AllLocations;
        Initialization = LoadLocationsAsync();
    }

    public override string Title => "Kartu Stok";

    public Task Initialization { get; }

    public ObservableCollection<LocationOption> Locations { get; } = [];
    public ObservableCollection<StockCardLine> Rows { get; } = [];

    public Guid? ProductId { get; private set; }

    [ObservableProperty] private string _productText = "Pilih barang dengan Barang [F1].";
    [ObservableProperty] private LocationOption _selectedLocation;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private StockCardLine? _selectedLine;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private FocusRequest? _focusRequest;

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

    /// <summary>F9 from Form Stok Total: that product and location, this month.</summary>
    public async Task ShowAsync(Guid productId, Guid? locationId)
    {
        await Initialization;
        ProductId = productId;
        SelectedLocation = Locations.FirstOrDefault(l => l.Id == locationId) ?? StockTotalViewModel.AllLocations;
        await ShowCardAsync();
    }

    /// <summary>"Tampilkan [Enter]": reload from the first page with the current filters.</summary>
    [RelayCommand]
    private async Task ShowCardAsync()
    {
        if (ProductId is not { } productId)
        {
            StatusMessage = "Pilih barang dulu (Barang [F1]).";
            return;
        }
        if (FromDate is not { } from || ToDate is not { } to)
        {
            StatusMessage = "Isi tanggal Dari dan Sampai.";
            return;
        }
        if (to < from)
        {
            StatusMessage = "Tanggal Sampai lebih awal dari tanggal Dari.";
            return;
        }

        try
        {
            var card = await _api.GetStockCardAsync(productId, SelectedLocation.Id, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), 0, PageSize);
            _card = card;
            ProductText = $"{card.Sku} - {card.Name} (satuan {card.BaseUnitCode})";
            Rows.Clear();
            Rows.Add(new StockCardLine(true, null, "", "Saldo Awal", null, null, null, null, null, 0m, 0m, card.OpeningBalance));
            _loaded = 0;
            Append(card);
            SummaryText = $"Saldo Awal {Number(card.OpeningBalance)}   ·   Masuk {Number(card.TotalIn)}   ·   " +
                          $"Keluar {Number(card.TotalOut)}   ·   Saldo Akhir {Number(card.ClosingBalance)} {card.BaseUnitCode}";
            StatusMessage = card.TotalRows == 0 ? "Tidak ada pergerakan stok pada periode ini." : null;
            SelectedLine = Rows[0];
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        FocusRequest = new FocusRequest("rows", ++_focusSequence);
    }

    /// <summary>"Tampilkan berikutnya": the next page of the same card.</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_card is null || !HasMore || ProductId is not { } productId)
            return;
        try
        {
            var page = await _api.GetStockCardAsync(productId, _card.LocationId, _card.From, _card.To, _loaded, PageSize);
            Append(page);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void Append(StockCard page)
    {
        foreach (var row in page.Rows)
            Rows.Add(Line(row, page.TimeZone));
        _loaded += page.Rows.Count;
        HasMore = _loaded < page.TotalRows;
        CountText = HasMore
            ? $"Menampilkan {_loaded.ToString("#,0", Indonesian)} dari {page.TotalRows.ToString("#,0", Indonesian)} pergerakan"
            : $"{page.TotalRows.ToString("#,0", Indonesian)} pergerakan";
    }

    private static StockCardLine Line(StockCardRow row, string timeZone) => new(
        false, BranchTime.ToLocal(row.OccurredAt, timeZone), row.DocType, row.DocLabel, row.DocId, row.DocNumber, row.UserName,
        row.LocationCode, row.ExpiryDate, row.QtyIn, row.QtyOut, row.Balance);

    /// <summary>F1: choose the product; the card is shown at once.</summary>
    [RelayCommand]
    private async Task PickProductAsync()
    {
        if (_dialogs.PickProduct("") is not { } picked)
            return;
        ProductId = picked.ProductId;
        await ShowCardAsync();
    }

    /// <summary>Double-click: the source document. Sales are shown in full (as their receipt); other types as a summary.</summary>
    [RelayCommand]
    private async Task OpenDocumentAsync(StockCardLine? line)
    {
        line ??= SelectedLine;
        if (line is null || line.IsOpening || line.DocId is not { } docId)
            return;

        if (line.DocType == StockDocTypes.Sale)
        {
            try
            {
                var sale = await _api.GetSaleAsync(docId);
                _dialogs.ShowDocumentDetail($"Penjualan {sale.Number}", "Dokumen sumber: penjualan.",
                    ReceiptFormatter.Format(ReceiptService.ToReceiptData(sale), ReceiptFormatter.Width80mm));
            }
            catch (NodeApiException ex)
            {
                StatusMessage = ex.Message;
            }
            return;
        }

        string[] summary =
        [
            $"Jenis Dokumen : {line.DocLabel}",
            $"No Dokumen    : {line.DocNumber ?? "-"}",
            $"Tanggal       : {line.TimeText}",
            $"Oleh          : {line.UserName ?? "-"}",
            $"Lokasi        : {line.LocationCode}",
            $"Tanggal Exp.  : {(line.ExpiryText.Length == 0 ? "-" : line.ExpiryText)}",
            $"Masuk         : {(line.InText.Length == 0 ? "0" : line.InText)}",
            $"Keluar        : {(line.OutText.Length == 0 ? "0" : line.OutText)}",
        ];
        _dialogs.ShowDocumentDetail($"{line.DocLabel} {line.DocNumber}".Trim(),
            $"Rincian lengkap dokumen {line.DocLabel} belum tersedia di node; ini ringkasan pergerakan stoknya.", summary);
    }

    /// <summary>F8 "Export to File": the opening balance and every movement of the period (all pages).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_card is null || ProductId is not { } productId)
        {
            StatusMessage = "Tampilkan kartu stok dulu.";
            return;
        }
        var path = _dialogs.AskSaveFile($"Kartu Stok {_card.Sku} {_card.From:yyyyMMdd}-{_card.To:yyyyMMdd}.xlsx");
        if (path is null)
            return;

        try
        {
            var lines = new List<StockCardLine> { Rows[0] };
            for (var offset = 0; ; offset += ExportPageSize)
            {
                var page = await _api.GetStockCardAsync(productId, _card.LocationId, _card.From, _card.To, offset, ExportPageSize);
                lines.AddRange(page.Rows.Select(r => Line(r, page.TimeZone)));
                if (page.Rows.Count == 0 || lines.Count - 1 >= page.TotalRows)
                    break;
            }

            XlsxWriter.Write(path, "Kartu Stok",
                ["Tanggal", "Jenis Dokumen", "No Dokumen", "Oleh", "Lokasi", "Tanggal Expired", "Masuk", "Keluar", "Saldo"],
                lines.Select(l => (IReadOnlyList<object?>)
                [
                    l.IsOpening ? null : l.TimeText, l.DocLabel, l.DocNumber, l.UserName, l.LocationCode,
                    l.ExpiryDate, l.IsOpening ? null : l.QtyIn, l.IsOpening ? null : l.QtyOut, l.Balance,
                ]));
            StatusMessage = $"{(lines.Count - 1).ToString("#,0", Indonesian)} pergerakan disimpan ke {path}.";
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"File tidak dapat disimpan: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => RaiseCloseRequested();

    private static string Number(decimal value) => value.ToString("#,0.####", Indonesian);
}
