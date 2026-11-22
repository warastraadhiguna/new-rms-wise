using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>One row of Daftar Mutasi Lokasi (time already in branch time).</summary>
public sealed record TransferListRow(Guid Id, string Number, DateTime LocalTime, string FromLocationCode, string ToLocationCode,
    string CreatedByName, int LineCount, string? Notes)
{
    public string TimeText => LocalTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// "Daftar Mutasi Lokasi": this node's transfers in a period (branch dates, default this month), newest first.
/// Enter / double-click shows the detail (lines, typed quantity and unit, expiry dates taken).
/// </summary>
public sealed partial class LocationTransferListViewModel : FormTab
{
    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private int _focusSequence;

    public LocationTransferListViewModel(INodeApi api, IDialogService dialogs, Func<DateOnly>? today = null)
    {
        _api = api;
        _dialogs = dialogs;
        var now = (today ?? (() => DateOnly.FromDateTime(DateTime.Now)))();
        _fromDate = new DateTime(now.Year, now.Month, 1);
        _toDate = now.ToDateTime(TimeOnly.MinValue);
        Initialization = ShowAsync();
    }

    public override string Title => "Daftar Mutasi Lokasi";

    public Task Initialization { get; }

    public ObservableCollection<TransferListRow> Rows { get; } = [];

    /// <summary>The user may create transfers (stock.transfer): the detail offers "Buat Mutasi Balik".</summary>
    public bool CanReverse { get; set; }

    /// <summary>"Buat Mutasi Balik" was chosen in the detail.</summary>
    public event EventHandler<LocationTransfer>? ReverseRequested;

    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private TransferListRow? _selectedRow;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private FocusRequest? _focusRequest;

    /// <summary>"Tampilkan [Enter]".</summary>
    [RelayCommand]
    private async Task ShowAsync()
    {
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
            var list = await _api.GetLocationTransfersAsync(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
            Rows.Clear();
            foreach (var t in list)
            {
                Rows.Add(new TransferListRow(t.Id, t.Number, BranchTime.ToLocal(t.TransferredAt, t.TimeZone), t.FromLocationCode,
                    t.ToLocationCode, t.CreatedByName, t.LineCount, t.Notes));
            }
            SelectedRow = Rows.FirstOrDefault();
            CountText = $"{Rows.Count} mutasi";
            StatusMessage = Rows.Count == 0 ? "Tidak ada mutasi pada periode ini." : null;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        FocusRequest = new FocusRequest("rows", ++_focusSequence);
    }

    /// <summary>Enter / double-click: the transfer detail, loaded again from the Node Service.</summary>
    [RelayCommand]
    private async Task OpenDetailAsync(TransferListRow? row)
    {
        row ??= SelectedRow;
        if (row is null)
            return;
        try
        {
            var transfer = await _api.GetLocationTransferAsync(row.Id);
            if (ReversalDetail.Show(_dialogs, transfer, CanReverse))
                ReverseRequested?.Invoke(this, transfer);
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => RaiseCloseRequested();
}
