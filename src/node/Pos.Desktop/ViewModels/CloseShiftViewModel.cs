using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>One non-cash method on the Tutup Shift summary.</summary>
public sealed record ShiftMethodRow(string Name, int Count, string AmountText);

/// <summary>
/// "Tutup Shift": the cashier counts the drawer and enters Kas Fisik. With blind count (setting shift.blind_count,
/// default on) the system figures stay hidden until the count is saved; without it the running summary is shown first.
/// Saving closes the shift (idempotent: a retry after a network failure sends the same request), prints the report
/// (or shows it on screen) and shows the summary with the difference (Lebih / Kurang / Pas, coloured).
/// Closing another cashier's shift needs Keterangan. "Selesai" ends the dialog; the main window then logs out.
/// </summary>
public sealed partial class CloseShiftViewModel : DialogViewModel
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly ReceiptService _receipts;
    private readonly ShiftInfo _shift;
    private int _focusSequence;

    public CloseShiftViewModel(INodeApi api, IDialogService dialogs, ReceiptService receipts, ShiftInfo shift, Guid userId)
    {
        _api = api;
        _dialogs = dialogs;
        _receipts = receipts;
        _shift = shift;
        IsOwner = shift.CashierId == userId;
        Initialization = LoadAsync();
        RequestFocus("counted");
    }

    public Task Initialization { get; }

    /// <summary>The logged-in user is the shift's cashier (otherwise Keterangan is required).</summary>
    public bool IsOwner { get; }

    public bool NotesRequired => !IsOwner;

    public string NotesLabel => NotesRequired ? "Keterangan (wajib)" : "Keterangan";

    public string ShiftNumber => _shift.Number;

    public string CashierName => Summary?.CashierName ?? _shift.CashierName ?? "";

    public string OpenedText => Summary is { } s
        ? BranchTime.ToLocal(s.OpenedAt, s.TimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        : "";

    public ObservableCollection<ShiftMethodRow> NonCash { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CashierName), nameof(OpenedText), nameof(IsClosed), nameof(CanEdit), nameof(HasFigures),
        nameof(IsBlindHint), nameof(OpeningText), nameof(CashSalesText), nameof(DownPaymentText), nameof(ReceivableText),
        nameof(ExpensesText), nameof(ExpectedText), nameof(CountedCashText), nameof(DifferenceLabel), nameof(DifferenceText),
        nameof(DifferenceKind), nameof(SaleCountText), nameof(CancellationCountText))]
    private ShiftSummary? _summary;

    [ObservableProperty] private string _countedText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FocusRequest? _focusRequest;

    public bool IsClosed => Summary?.IsClosed == true;

    public bool CanEdit => !IsClosed;

    public bool HasFigures => Summary?.HasFigures == true;

    /// <summary>Blind count before saving: "count first" hint instead of the figures.</summary>
    public bool IsBlindHint => Summary is { HasFigures: false };

    public string OpeningText => Money(Summary?.OpeningCash);
    public string CashSalesText => Money(Summary?.CashSales);
    public string DownPaymentText => Money(Summary?.CreditDownPayments);
    public string ReceivableText => Money(Summary?.ReceivableCash);
    public string ExpensesText => Summary?.CashExpenses is { } e and not 0 ? Money(-e) : Money(Summary?.CashExpenses);
    public string ExpectedText => Money(Summary?.ExpectedCash);
    public string CountedCashText => Money(Summary?.CountedCash);
    public string SaleCountText => Summary?.SaleCount?.ToString(Indonesian) ?? "";
    public string CancellationCountText => Summary?.CancellationCount?.ToString(Indonesian) ?? "";

    /// <summary>"Selisih (Kurang)", "Selisih (Lebih)" or "Selisih (Pas)".</summary>
    public string DifferenceLabel => Summary?.Difference is { } d ? $"Selisih ({ShiftClosing.Label(d)})" : "Selisih";

    public string DifferenceText => Money(Summary?.Difference);

    /// <summary>"Over" (green), "Short" (red) or "Exact"; empty before closing.</summary>
    public string DifferenceKind => Summary?.Difference is { } d ? ShiftClosing.Kind(d).ToString() : "";

    private async Task LoadAsync()
    {
        try
        {
            Apply(await _api.GetShiftClosingAsync(_shift.Id));
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void Apply(ShiftSummary summary)
    {
        Summary = summary;
        NonCash.Clear();
        foreach (var method in summary.NonCash ?? [])
            NonCash.Add(new ShiftMethodRow(method.Name, method.Count, Money(method.Amount)));
    }

    /// <summary>Enter / "Simpan": closes the shift with the counted cash (after a confirmation).</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || IsClosed)
            return;
        if (!MoneyInput.TryParse(CountedText, out var counted))
        {
            StatusMessage = "Isi Kas Fisik: jumlah uang di laci, misalnya 1.250.000.";
            RequestFocus("counted");
            return;
        }
        var notes = string.IsNullOrWhiteSpace(NotesText) ? null : NotesText.Trim();
        if (NotesRequired && notes is null)
        {
            StatusMessage = $"Isi Keterangan: shift ini milik {CashierName}.";
            RequestFocus("notes");
            return;
        }
        if (!_dialogs.Confirm($"Tutup shift {ShiftNumber} dengan Kas Fisik {Money(counted)}? Setelah ditutup, shift tidak bisa diubah."))
        {
            RequestFocus("counted");
            return;
        }

        IsBusy = true;
        try
        {
            Apply(await _api.CloseShiftAsync(new CloseShiftRequest(_shift.Id, counted, notes)));
            var printed = _receipts.PrintShiftReport(Summary!);
            StatusMessage = printed.Printed ? "Shift ditutup. Laporan tutup shift dicetak." : $"Shift ditutup. {printed.Message}";
            RequestFocus("finish");
        }
        catch (NodeApiException ex) when (ex.Code is "node_unreachable")
        {
            StatusMessage = $"{ex.Message} Tekan Simpan lagi (shift tidak akan ditutup dua kali).";
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

    /// <summary>Enter anywhere: Simpan before closing, Selesai after.</summary>
    [RelayCommand]
    private Task EnterAsync()
    {
        if (IsClosed)
        {
            Finish();
            return Task.CompletedTask;
        }
        return SaveAsync();
    }

    /// <summary>"Cetak Ulang [F5]": the report again, marked CETAK ULANG.</summary>
    [RelayCommand]
    private void Reprint()
    {
        if (Summary is { IsClosed: true } summary)
        {
            var printed = _receipts.PrintShiftReport(summary, reprint: true);
            StatusMessage = printed.Printed ? "Laporan tutup shift dicetak ulang." : printed.Message;
        }
    }

    /// <summary>"Selesai": after closing, ends the dialog (the main window logs out).</summary>
    [RelayCommand]
    private void Finish()
    {
        if (IsClosed)
            DialogResult = true;
    }

    /// <summary>Esc: before closing = cancel (nothing changes); after closing = Selesai.</summary>
    [RelayCommand]
    private void Escape() => DialogResult = IsClosed;

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);

    private static string Money(decimal? value) => value is { } v ? v.ToString("#,0.####", Indonesian) : "";
}
