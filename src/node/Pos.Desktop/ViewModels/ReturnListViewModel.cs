using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Printing;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>One row of Data Retur Penjualan (branch time).</summary>
public sealed record ReturnListItem(SaleReturnSummary Row, DateTime LocalTime)
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public Guid Id => Row.Id;
    public string Number => Row.Number;
    public string TimeText => LocalTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    public string SaleNumber => Row.SaleNumber;
    public string? CustomerName => Row.CustomerName;
    public string CreatedByName => Row.CreatedByName;

    /// <summary>"Tukar Barang (belum dipakai)" while the exchange value is still open.</summary>
    public string SettlementText => ReturnSettlements.Label(Row.Settlement)
                                    + (Row.Settlement == ReturnSettlements.Exchange && !Row.ExchangeUsed ? " (belum dipakai)" : "");

    public string TotalText => Row.Total.ToString("#,0.####", Indonesian);
    public string? Notes => Row.Notes;
}

/// <summary>
/// "Data Retur Penjualan": returns of a period (branch dates, default today). F1 Tambah Retur (type or scan the nota
/// number), F11 / Enter Lihat Retur (detail as the Bukti Retur; an unused Tukar Barang offers "Tukar Barang"), F5 Cetak Ulang.
/// </summary>
public sealed partial class ReturnListViewModel : FormTab
{
    public const string ExchangeAction = "Tukar Barang";

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly ReceiptService _receipts;
    private int _focusSequence;

    public ReturnListViewModel(INodeApi api, IDialogService dialogs, ReceiptService receipts, Func<DateOnly>? today = null)
    {
        _api = api;
        _dialogs = dialogs;
        _receipts = receipts;
        var now = (today ?? (() => DateOnly.FromDateTime(DateTime.Now)))();
        _fromDate = now.ToDateTime(TimeOnly.MinValue);
        _toDate = now.ToDateTime(TimeOnly.MinValue);
        Initialization = ShowAsync();
    }

    public override string Title => "Data Retur Penjualan";

    public Task Initialization { get; }

    public ObservableCollection<ReturnListItem> Rows { get; } = [];

    /// <summary>F1 "Tambah Retur": the nota number typed / scanned.</summary>
    public event EventHandler<string>? NewReturnRequested;

    /// <summary>"Tukar Barang" chosen on an unused exchange return.</summary>
    public event EventHandler<SaleReturnInfo>? ExchangeRequested;

    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private ReturnListItem? _selectedRow;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private FocusRequest? _focusRequest;

    /// <summary>"Tampilkan [Enter]".</summary>
    [RelayCommand]
    public async Task ShowAsync()
    {
        if (FromDate is not { } from || ToDate is not { } to)
        {
            StatusMessage = "Isi tanggal awal dan akhir.";
            return;
        }
        if (to < from)
        {
            StatusMessage = "Tanggal akhir lebih awal dari tanggal awal.";
            return;
        }
        try
        {
            var list = await _api.GetReturnsAsync(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
            var keep = SelectedRow?.Id;
            Rows.Clear();
            foreach (var r in list)
                Rows.Add(new ReturnListItem(r, BranchTime.ToLocal(r.ReturnedAt, r.TimeZone)));
            SelectedRow = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
            CountText = $"{Rows.Count} retur";
            StatusMessage = Rows.Count == 0 ? "Tidak ada retur pada periode ini." : null;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("rows");
    }

    /// <summary>F1 "Tambah Retur [F1]".</summary>
    [RelayCommand]
    private void NewReturn()
    {
        if (_dialogs.AskText("Tambah Retur", "Ketik atau scan No Nota:") is { } number)
            NewReturnRequested?.Invoke(this, number);
    }

    /// <summary>F11 / Enter "Lihat Retur".</summary>
    [RelayCommand]
    private async Task ViewAsync()
    {
        if (SelectedRow is not { } row)
            return;
        try
        {
            var ret = await _api.GetReturnAsync(row.Id);
            var lines = ReturnReceiptFormatter.Format(ReceiptService.ToReturnReceiptData(ret), ReceiptFormatter.Width80mm);
            var message = ret.IsUnusedExchange ? "Nilai retur ini belum dipakai untuk tukar barang."
                : ret.ExchangeSaleNumber is { } sale ? $"Dipakai untuk tukar barang di nota {sale}."
                : $"Penyelesaian: {ReturnSettlements.Label(ret.Settlement)}.";
            if (_dialogs.ShowDocumentDetail($"Retur {ret.Number}", message, lines, ret.IsUnusedExchange ? ExchangeAction : null))
                ExchangeRequested?.Invoke(this, ret);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>F5 "Cetak Ulang": the Bukti Retur again (CETAK ULANG).</summary>
    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (SelectedRow is not { } row)
            return;
        try
        {
            var printed = _receipts.PrintReturn(await _api.GetReturnAsync(row.Id), reprint: true);
            StatusMessage = printed.Printed ? $"Bukti retur {printed.Number} dicetak ulang." : printed.Message;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => RaiseCloseRequested();

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
