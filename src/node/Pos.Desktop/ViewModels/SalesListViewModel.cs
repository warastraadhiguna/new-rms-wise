using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Printing;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>One row of Data Penjualan with the texts of the RMS 2 columns (branch time).</summary>
public sealed record SalesListItem(SalesListRow Row, DateTime LocalTime)
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public Guid Id => Row.Id;
    public string Number => Row.Number;
    public string DateText => LocalTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public string TimeText => LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string Member => Row.CustomerName is { } name ? $"{Row.CustomerCode} - {name}" : "";
    public string CashierName => Row.CashierName;
    public string PaymentMethods => Row.PaymentMethods;
    public string TotalText => Money(Row.Total);
    public string DiscountPctText => Row.DiscountPct is { } pct and not 0 ? pct.ToString("#,0.##", Indonesian) : "";
    public string DiscountAmountText => Row.DiscountAmount == 0 ? "" : Money(Row.DiscountAmount);
    public string GrandTotalText => Money(Row.GrandTotal);
    public string StatusText => SaleStatuses.Label(Row.Status);
    public string DueDateText => Row.DueDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
    public string OutstandingText => Row.Outstanding == 0 ? "" : Money(Row.Outstanding);
    public string? Notes => Row.Notes;
    public bool IsVoid => Row.Status == SaleStatuses.Void;

    private static string Money(decimal value) => value.ToString("#,0.####", Indonesian);
}

/// <summary>
/// "Data Penjualan" (RMS 2, menu Penjualan): sales of a period (branch dates), Cari by nota, member or cashier with the
/// standard search keys, RMS 2 columns. A cashier sees only H-N days (setting sales.cashier_history_days, default 15);
/// sale.history has no limit. F11 / Enter "Lihat/Cetak": the nota in receipt layout with Cetak Ulang; F8 export.
/// </summary>
public sealed partial class SalesListViewModel : FormTab
{
    public const int PageSize = 500;
    public const int ExportPageSize = 1000;
    public const string ReprintAction = "Cetak Ulang";

    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly ReceiptService _receipts;
    private readonly SessionState _session;
    private (Guid SaleId, Guid VoidId, ReasonChoice Reason, string? ApprovalToken)? _pendingVoid;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _searchCts;
    private int _focusSequence;

    public SalesListViewModel(INodeApi api, IDialogService dialogs, ReceiptService receipts, SessionState session,
        Func<DateOnly>? today = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _dialogs = dialogs;
        _receipts = receipts;
        _session = session;
        _delay = delay ?? Task.Delay;
        var now = (today ?? (() => DateOnly.FromDateTime(DateTime.Now)))();
        _fromDate = now.ToDateTime(TimeOnly.MinValue);
        _toDate = now.ToDateTime(TimeOnly.MinValue);
        RequestFocus("search");
        Reload(debounce: false);
        Initialization = PendingSearch;
    }

    public override string Title => "Data Penjualan";

    public Task Initialization { get; }

    /// <summary>The running (debounced) reload.</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public ObservableCollection<SalesListItem> Rows { get; } = [];

    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SalesListItem? _selectedRow;
    [ObservableProperty] private int _total;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private FocusRequest? _focusRequest;

    /// <summary>The first date a cashier may choose (null = no limit); from the Node Service.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LimitText))]
    private DateTime? _earliestDate;

    /// <summary>"Tanggal awal yang dapat dilihat kasir hanya H-15 (07/11/2026)".</summary>
    public string? LimitText => EarliestDate is { } e
        ? $"Tanggal awal yang dapat dilihat kasir hanya sampai {e.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}."
        : null;

    public string CountText => Rows.Count == Total
        ? $"{Total.ToString("#,0", Indonesian)} nota"
        : $"Menampilkan {Rows.Count.ToString("#,0", Indonesian)} dari {Total.ToString("#,0", Indonesian)} nota — persempit pencarian.";

    partial void OnSearchTextChanged(string value) => Reload(debounce: true);

    /// <summary>"Tampilkan [Enter]" in the date filter.</summary>
    [RelayCommand]
    private Task ShowAsync()
    {
        Reload(debounce: false);
        return PendingSearch;
    }

    private void Reload(bool debounce)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        PendingSearch = ReloadAsync(debounce, _searchCts.Token);
    }

    private bool TryPeriod(out DateOnly from, out DateOnly to)
    {
        from = to = default;
        if (FromDate is not { } f || ToDate is not { } t)
        {
            StatusMessage = "Isi tanggal awal dan akhir.";
            return false;
        }
        if (t < f)
        {
            StatusMessage = "Tanggal akhir lebih awal dari tanggal awal.";
            return false;
        }
        if (EarliestDate is { } earliest && f.Date < earliest.Date)
        {
            StatusMessage = LimitText;
            return false;
        }
        (from, to) = (DateOnly.FromDateTime(f), DateOnly.FromDateTime(t));
        return true;
    }

    private async Task ReloadAsync(bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce)
                await _delay(SalesViewModel.SearchDelay, token);
            if (token.IsCancellationRequested || !TryPeriod(out var from, out var to))
                return;
            var page = await _api.GetSalesAsync(from, to, SearchText.Trim(), 0, PageSize, token);
            if (token.IsCancellationRequested)
                return;

            var keep = SelectedRow?.Id;
            Rows.Clear();
            foreach (var row in page.Items)
                Rows.Add(new SalesListItem(row, BranchTime.ToLocal(row.SoldAt, page.TimeZone)));
            Total = page.Total;
            EarliestDate = page.EarliestDate?.ToDateTime(TimeOnly.MinValue);
            SelectedRow = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
            StatusMessage = page.Total == 0 ? "Tidak ada penjualan pada periode / pencarian ini." : null;
            OnPropertyChanged(nameof(CountText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>F11 / Enter "Lihat/Cetak": the nota in receipt layout; "Cetak Ulang" prints it again (CETAK ULANG).</summary>
    [RelayCommand]
    private async Task ViewAsync()
    {
        if (SelectedRow is not { } row)
            return;
        try
        {
            var sale = await _api.GetSaleAsync(row.Id);
            var lines = ReceiptFormatter.Format(ReceiptService.ToReceiptData(sale), ReceiptFormatter.Width80mm, reprint: true);
            var message = row.IsVoid ? "Nota ini sudah di-void." : $"Status: {row.StatusText}.";
            if (_dialogs.ShowDocumentDetail($"Penjualan {sale.Number}", message, lines, ReprintAction))
            {
                var printed = await _receipts.PrintAsync(row.Id, reprint: true);
                StatusMessage = printed.Printed ? $"Nota {printed.Number} dicetak ulang." : printed.Message;
            }
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("rows");
    }

    /// <summary>
    /// Shift+Del "Void" (replaces RMS 2 Hapus): only while the nota's shift is open (otherwise Retur). Alasan + Keterangan;
    /// without sale.void a supervisor approves. A retry after a network failure sends the same void (same id).
    /// </summary>
    [RelayCommand]
    private async Task VoidAsync()
    {
        if (SelectedRow is not { } row)
            return;
        if (row.IsVoid)
        {
            StatusMessage = $"Nota {row.Number} sudah di-void.";
            return;
        }
        if (!row.Row.ShiftOpen)
        {
            StatusMessage = "Shift sudah ditutup, gunakan Retur [F12].";
            return;
        }

        try
        {
            if (_pendingVoid is not { } pending || pending.SaleId != row.Id)
            {
                var reasons = await _api.GetReasonsAsync("SALE_VOID");
                if (_dialogs.AskReason($"Void Nota {row.Number}", "Alasan void nota ini:", reasons) is not { } reason)
                    return;
                string? token = null;
                if (!_session.Has(Pos.Shared.Auth.Permissions.SaleVoid))
                {
                    if (_dialogs.AskSupervisor($"Void nota {row.Number}") is not { } supervisor)
                        return;
                    token = (await _api.ApproveAsync(Pos.Shared.Auth.Permissions.SaleVoid, supervisor.Username, supervisor.Password)).ApprovalToken;
                }
                if (!_dialogs.Confirm($"Void nota {row.Number} ({row.GrandTotalText})? Stok kembali dan nota tidak bisa dipakai lagi."))
                    return;
                pending = (row.Id, Guid.CreateVersion7(), reason, token);
                _pendingVoid = pending;
            }

            await _api.VoidSaleAsync(row.Id, new VoidSaleRequest(pending.VoidId, pending.Reason.ReasonId, pending.Reason.Notes, pending.ApprovalToken));
            _pendingVoid = null;
            await ShowAsync();
            StatusMessage = $"Nota {row.Number} di-void ({pending.Reason.ReasonName}).";
        }
        catch (NodeApiException ex) when (ex.Code is "node_unreachable")
        {
            StatusMessage = $"{ex.Message} Tekan Void lagi untuk mengirim ulang void yang sama.";
        }
        catch (NodeApiException ex)
        {
            _pendingVoid = null;
            StatusMessage = ex.Message;
        }
        RequestFocus("rows");
    }

    /// <summary>F12 "Retur": opens Retur Penjualan for the selected nota.</summary>
    public event EventHandler<Guid>? ReturnRequested;

    [RelayCommand]
    private void Return()
    {
        if (SelectedRow is not { } row)
            return;
        if (row.IsVoid)
        {
            StatusMessage = $"Nota {row.Number} sudah di-void; tidak bisa diretur.";
            return;
        }
        ReturnRequested?.Invoke(this, row.Id);
    }

    /// <summary>F8 "Export to File": every nota of the period and search (all pages).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!TryPeriod(out var from, out var to))
            return;
        var path = _dialogs.AskSaveFile($"Data Penjualan {from:yyyyMMdd}-{to:yyyyMMdd}.xlsx");
        if (path is null)
            return;
        try
        {
            var items = new List<SalesListItem>();
            for (var offset = 0; ; offset += ExportPageSize)
            {
                var page = await _api.GetSalesAsync(from, to, SearchText.Trim(), offset, ExportPageSize);
                items.AddRange(page.Items.Select(r => new SalesListItem(r, BranchTime.ToLocal(r.SoldAt, page.TimeZone))));
                if (page.Items.Count == 0 || items.Count >= page.Total)
                    break;
            }
            XlsxWriter.Write(path, "Data Penjualan",
                ["No Nota", "Tanggal Nota", "Waktu", "Member", "Kasir", "Cara Bayar", "Total", "Diskon (%)", "Potongan", "Grand Total",
                 "Status", "Jatuh Tempo", "Total Tertanggung", "Keterangan"],
                items.Select(i => (IReadOnlyList<object?>)
                [
                    i.Number, DateOnly.FromDateTime(i.LocalTime), i.TimeText, i.Member, i.CashierName, i.PaymentMethods,
                    i.Row.Total, i.Row.DiscountPct, i.Row.DiscountAmount, i.Row.GrandTotal, i.StatusText, i.Row.DueDate,
                    i.Row.Outstanding, i.Notes,
                ]));
            StatusMessage = $"{items.Count.ToString("#,0", Indonesian)} nota disimpan ke {path}.";
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

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
