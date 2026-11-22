using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Auth;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>One nota line in the return form: sold, already returned, what is returned now and in which condition.</summary>
public sealed partial class ReturnLineItem(ReturnSourceLine line, int currencyDecimals) : ObservableObject
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public ReturnSourceLine Line { get; } = line;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QtyText), nameof(Amount), nameof(AmountText))]
    private decimal _qty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText))]
    private bool _damaged;

    public int No => Line.LineNo;
    public string Code => Line.Sku;
    public string Name => Line.Name;
    public string UnitCode => Line.UnitCode;
    public string SoldText => Number(Line.SoldQty);
    public string ReturnedText => Line.ReturnedQty == 0 ? "" : Number(Line.ReturnedQty);
    public string ReturnableText => Number(Line.ReturnableQty);
    public string PriceText => Number(Line.NetUnitPrice);
    public string QtyText => Qty == 0 ? "" : Number(Qty);
    public string ConditionText => Damaged ? "RUSAK" : "BAIK";

    /// <summary>Refund of this line, computed like the server (cumulative net price).</summary>
    public decimal Amount => Qty <= 0 ? 0m
        : ReturnPricing.RefundAmount(Line.NetLineTotal, Line.SoldQty, Line.ReturnedQty, Qty, currencyDecimals);

    public string AmountText => Amount == 0 ? "" : Number(Amount);

    private static string Number(decimal value) => value.ToString("#,0.####", Indonesian);
}

/// <summary>A settlement choice in the form (only POTONG PIUTANG depends on the nota being on credit).</summary>
public sealed record SettlementOption(string Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Retur Penjualan" (10c): opened from Data Penjualan (F12 on a nota) or Data Retur Penjualan (Tambah Retur: nota number).
/// F8 sets the returned quantity of the selected line (at most sold minus already returned), F7 switches Kondisi BAIK /
/// RUSAK, Penyelesaian UANG KEMBALI / TUKAR BARANG / POTONG PIUTANG (credit notas). F6 Simpan: Alasan + Keterangan,
/// a supervisor after returns.window_days (without sale.return.late), confirmation; prints the Bukti Retur. TUKAR BARANG
/// continues in Form Penjualan with the return's value as payment. A retry after a network failure sends the same return.
/// </summary>
public sealed partial class ReturnFormViewModel : FormTab
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly ReceiptService _receipts;
    private readonly SessionState _session;
    private (Guid Id, CreateSaleReturnRequest Request)? _pending;
    private int _focusSequence;

    public ReturnFormViewModel(INodeApi api, IDialogService dialogs, ReceiptService receipts, SessionState session)
    {
        _api = api;
        _dialogs = dialogs;
        _receipts = receipts;
        _session = session;
        Settlements.Add(Refund);
        Settlements.Add(Exchange);
        _selectedSettlement = Refund;
    }

    public static readonly SettlementOption Refund = new(ReturnSettlements.Refund, "UANG KEMBALI");
    public static readonly SettlementOption Exchange = new(ReturnSettlements.Exchange, "TUKAR BARANG");
    public static readonly SettlementOption Reduce = new(ReturnSettlements.ReduceReceivable, "POTONG PIUTANG");

    public override string Title => "Retur Penjualan";

    public ObservableCollection<ReturnLineItem> Lines { get; } = [];
    public ObservableCollection<SettlementOption> Settlements { get; } = [];

    /// <summary>TUKAR BARANG was saved: continue in Form Penjualan with this return as payment.</summary>
    public event EventHandler<SaleReturnInfo>? ExchangeRequested;

    /// <summary>A return was saved (Data Retur Penjualan reloads).</summary>
    public event EventHandler<SaleReturnInfo>? Saved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotaText), nameof(DateText), nameof(MemberText), nameof(CashierText), nameof(WindowText),
        nameof(HasSource), nameof(TotalText))]
    private ReturnSource? _source;

    [ObservableProperty] private ReturnLineItem? _selectedLine;
    [ObservableProperty] private SettlementOption _selectedSettlement;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FocusRequest? _focusRequest;

    public bool HasSource => Source is not null;

    public string NotaText => Source?.SaleNumber ?? "";

    public string DateText => Source is { } s
        ? BranchTime.ToLocal(s.SoldAt, s.TimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "";

    public string MemberText => Source?.CustomerName is { } name ? $"{Source.CustomerCode} - {name}" : "-";

    public string CashierText => Source?.CashierName ?? "";

    /// <summary>Only when the nota is past the window: the save asks a supervisor.</summary>
    public string? WindowText => Source is { WithinWindow: false } s
        ? $"Nota lewat batas retur {s.WindowDays} hari: perlu persetujuan supervisor."
        : null;

    public decimal Total => Lines.Sum(l => l.Amount);

    public string TotalText => Total.ToString("#,0.####", Indonesian);

    /// <summary>Loads the nota; lines start at quantity 0 (nothing returned until F8).</summary>
    public async Task LoadAsync(Guid? saleId, string? number = null)
    {
        if (Lines.Any(l => l.Qty > 0) && !_dialogs.Confirm("Retur yang sedang diisi belum disimpan. Ganti dengan nota lain?"))
            return;
        try
        {
            Apply(await _api.GetReturnSourceAsync(saleId, number));
            StatusMessage = Source!.Voided ? "Nota ini sudah di-void; tidak bisa diretur."
                : Lines.All(l => l.Line.ReturnableQty == 0) ? "Semua barang di nota ini sudah diretur."
                : null;
        }
        catch (NodeApiException ex)
        {
            StatusMessage = ex.Message;
        }
        RequestFocus("lines");
    }

    private void Apply(ReturnSource source)
    {
        _pending = null;
        Lines.Clear();
        foreach (var line in source.Lines)
        {
            var item = new ReturnLineItem(line, source.CurrencyDecimals);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ReturnLineItem.Amount))
                {
                    _pending = null;
                    OnPropertyChanged(nameof(Total));
                    OnPropertyChanged(nameof(TotalText));
                }
                else if (e.PropertyName == nameof(ReturnLineItem.Damaged))
                    _pending = null;
            };
            Lines.Add(item);
        }
        Source = source;
        Settlements.Remove(Reduce);
        if (source.CreditAmount > 0 && source.Outstanding > 0)
            Settlements.Add(Reduce);
        SelectedSettlement = Refund;
        SelectedLine = Lines.FirstOrDefault(l => l.Line.ReturnableQty > 0) ?? Lines.FirstOrDefault();
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalText));
    }

    partial void OnSelectedSettlementChanged(SettlementOption value) => _pending = null;

    /// <summary>F8 "Atur Jumlah": the returned quantity of the selected line (0 = not returned).</summary>
    [RelayCommand]
    private void ChangeQty()
    {
        if (SelectedLine is not { } line)
            return;
        if (line.Line.ReturnableQty == 0)
        {
            StatusMessage = $"{line.Name} sudah diretur semua.";
            return;
        }
        if (_dialogs.AskQuantity($"{line.Name} (maks {line.ReturnableText} {line.UnitCode})", line.Qty) is { } qty)
        {
            if (qty < 0 || qty > line.Line.ReturnableQty)
                StatusMessage = $"Jumlah retur {line.Name} maksimal {line.ReturnableText} {line.UnitCode}.";
            else if (!line.Line.AllowDecimal && decimal.Truncate(qty) != qty)
                StatusMessage = $"Jumlah {line.UnitCode} harus bilangan bulat.";
            else
            {
                line.Qty = qty;
                StatusMessage = null;
            }
        }
        RequestFocus("lines");
    }

    /// <summary>F7 "Kondisi": BAIK (back to the selling location) / RUSAK (to the DAMAGED location).</summary>
    [RelayCommand]
    private void ToggleCondition()
    {
        if (SelectedLine is { } line)
            line.Damaged = !line.Damaged;
    }

    /// <summary>F6 "Simpan".</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || Source is not { } source)
            return;
        var returned = Lines.Where(l => l.Qty > 0).ToList();
        if (returned.Count == 0)
        {
            StatusMessage = "Isi jumlah retur dengan Atur Jumlah [F8].";
            RequestFocus("lines");
            return;
        }

        try
        {
            if (_pending is not { } pending)
            {
                var reasons = await _api.GetReasonsAsync("RETURN");
                if (_dialogs.AskReason($"Retur Nota {source.SaleNumber}", "Alasan retur:", reasons) is not { } reason)
                    return;
                string? token = null;
                if (!source.WithinWindow && !_session.Has(Permissions.SaleReturnLate))
                {
                    if (_dialogs.AskSupervisor($"Retur nota {source.SaleNumber} lewat {source.WindowDays} hari") is not { } supervisor)
                        return;
                    token = (await _api.ApproveAsync(Permissions.SaleReturnLate, supervisor.Username, supervisor.Password)).ApprovalToken;
                }
                if (!_dialogs.Confirm($"Simpan retur nota {source.SaleNumber} senilai {TotalText} ({SelectedSettlement.Label})?"))
                    return;
                var id = Guid.CreateVersion7();
                pending = (id, new CreateSaleReturnRequest(id, source.SaleId, SelectedSettlement.Code, reason.ReasonId, reason.Notes,
                    returned.Select(l => new SaleReturnLineRequest(l.Line.SaleLineId, l.Qty, l.Damaged ? "DAMAGED" : "GOOD")).ToList(), token));
                _pending = pending;
            }

            IsBusy = true;
            var saved = await _api.CreateReturnAsync(pending.Request);
            _pending = null;
            var printed = _receipts.PrintReturn(saved);
            Saved?.Invoke(this, saved);
            Apply(await _api.GetReturnSourceAsync(source.SaleId, null));
            StatusMessage = $"Retur {saved.Number} tersimpan ({ReturnSettlements.Label(saved.Settlement)} {saved.Total.ToString("#,0.####", Indonesian)})."
                            + (printed.Printed ? "" : $" {printed.Message}");
            if (saved.Settlement == ReturnSettlements.Exchange)
                ExchangeRequested?.Invoke(this, saved);
        }
        catch (NodeApiException ex) when (ex.Code is "node_unreachable")
        {
            StatusMessage = $"{ex.Message} Tekan F6 lagi untuk mengirim ulang retur yang sama (tidak akan tercatat dua kali).";
        }
        catch (NodeApiException ex)
        {
            _pending = null;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        RequestFocus("lines");
    }

    [RelayCommand]
    private void Close()
    {
        if (Lines.Any(l => l.Qty > 0) && !_dialogs.Confirm("Retur belum disimpan. Tutup form ini?"))
            return;
        RaiseCloseRequested();
    }

    public override void RequestClose() => CloseCommand.Execute(null);

    private void RequestFocus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);
}
