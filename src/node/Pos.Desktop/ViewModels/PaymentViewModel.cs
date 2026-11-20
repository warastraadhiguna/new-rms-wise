using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Auth;
using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// F6 "Form Bayar Nota", exactly as RMS 2 (docs/rms2/form-bayar.png): Cara Bayar (TUNAI first),
/// Total (cart, read-only), [Potongan|Diskon] + value, Grand Total (after discount; cash-rounded for
/// TUNAI), Bayar, Kembali, Keterangan, Referensi (only DEBIT/QRIS/TRANSFER). Focus starts on Bayar.
/// Totals and validation use the same Pos.Shared rules as the server. The sale id is fixed for this cart,
/// so a retry after a network failure cannot create a second sale.
/// </summary>
public sealed partial class PaymentViewModel : DialogViewModel
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    private readonly INodeApi _api;
    private readonly IDialogService _dialogs;
    private readonly SalesConfig _config;
    private readonly Cart _cart;
    private readonly CustomerItem? _customer;
    private int _focusSequence;
    private bool _bayarEdited;
    private bool _recalculating;

    public PaymentViewModel(INodeApi api, IDialogService dialogs, SalesConfig config, Cart cart, CustomerItem? customer, Guid saleId)
    {
        _api = api;
        _dialogs = dialogs;
        _config = config;
        _cart = cart;
        _customer = customer;
        SaleId = saleId;
        Total = cart.Total;
        _selectedMethod = config.PaymentMethods.FirstOrDefault(m => m.IsCash) ?? config.PaymentMethods.FirstOrDefault();
        Recalculate();
        Focus("bayar");
    }

    public Guid SaleId { get; }

    /// <summary>"Total": the cart before Potongan/Diskon.</summary>
    public decimal Total { get; }

    public IReadOnlyList<PaymentMethodInfo> PaymentMethods => _config.PaymentMethods;

    /// <summary>The small drop-down: Potongan (Rp, default) or Diskon (%).</summary>
    public IReadOnlyList<string> DiscountModes { get; } = ["Potongan", "Diskon"];

    public string? CustomerName => _customer?.Display;
    public SaleResult? Result { get; private set; }

    [ObservableProperty] private PaymentMethodInfo? _selectedMethod;
    [ObservableProperty] private bool _isNominalDiscount = true;
    [ObservableProperty] private string _discountText = "0";
    [ObservableProperty] private decimal _discountAmount;

    /// <summary>"Grand Total": after Potongan/Diskon, cash-rounded for TUNAI.</summary>
    [ObservableProperty] private decimal _grandTotal;

    [ObservableProperty] private string _bayarText = "";
    [ObservableProperty] private decimal _change;
    [ObservableProperty] private string _referenceText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private FocusRequest? _focusRequest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(BayarEnterCommand))]
    private bool _isBusy;

    /// <summary>Index for the drop-down: 0 = Potongan, 1 = Diskon.</summary>
    public int DiscountModeIndex
    {
        get => IsNominalDiscount ? 0 : 1;
        set => IsNominalDiscount = value != 1;
    }

    /// <summary>Referensi is used for DEBIT, QRIS and TRANSFER, not for TUNAI or KREDIT.</summary>
    public bool IsReferenceEnabled => SelectedMethod is { IsCash: false, IsCredit: false };

    partial void OnSelectedMethodChanged(PaymentMethodInfo? value)
    {
        _bayarEdited = false;
        if (!IsReferenceEnabled)
            ReferenceText = "";
        OnPropertyChanged(nameof(IsReferenceEnabled));
        OnPropertyChanged(nameof(IsCredit));
        OnPropertyChanged(nameof(BayarLabel));
        OnPropertyChanged(nameof(ChangeLabel));
        Recalculate();
    }

    partial void OnIsNominalDiscountChanged(bool value)
    {
        OnPropertyChanged(nameof(DiscountModeIndex));
        Recalculate();
    }

    partial void OnDiscountTextChanged(string value) => Recalculate();

    partial void OnBayarTextChanged(string value)
    {
        if (!_recalculating)
            _bayarEdited = true;
        UpdateChange();
    }

    private bool IsCash => SelectedMethod?.IsCash == true;

    /// <summary>KREDIT: Bayar is the down payment (DP), Kembali is the remaining receivable.</summary>
    public bool IsCredit => SelectedMethod?.IsCredit == true;

    public string BayarLabel => IsCredit ? "Uang Muka (DP)" : "Bayar";

    public string ChangeLabel => IsCredit ? "Sisa Piutang" : "Kembali";

    /// <summary>KREDIT was saved without a Member: the dialog closed so the cashier can choose one in the form.</summary>
    public bool MemberRequired { get; private set; }

    private CashRoundingSettings Rounding => new(_config.CashRoundingStep, CashRoundingModes.Parse(_config.CashRoundingMode));

    private bool TryReadDiscount(out HeaderDiscount discount)
    {
        discount = HeaderDiscount.None;
        if (!MoneyInput.TryParse(string.IsNullOrWhiteSpace(DiscountText) ? "0" : DiscountText, out var value))
            return false;
        discount = value == 0 ? HeaderDiscount.None : IsNominalDiscount ? HeaderDiscount.Nominal(value) : HeaderDiscount.Percent(value);
        return true;
    }

    private void Recalculate()
    {
        _recalculating = true;
        try
        {
            ErrorMessage = null;
            if (!TryReadDiscount(out var discount))
            {
                ErrorMessage = "Diskon tidak valid.";
                discount = HeaderDiscount.None;
            }

            try
            {
                DiscountAmount = SaleCalculator.HeaderDiscountAmount(discount, Total, _config.CurrencyDecimals);
            }
            catch (SaleValidationException)
            {
                ErrorMessage = "Diskon tidak valid.";
                DiscountAmount = 0m;
            }

            // DEBIT/QRIS/TRANSFER pay exactly the grand total (RMS 2); cash and the credit DP are typed by the cashier.
            if (!IsCash && !IsCredit)
            {
                GrandTotal = ComputeGrandTotal();
                BayarText = Format(GrandTotal);
            }
            else if (!_bayarEdited)
            {
                BayarText = "";
            }
            GrandTotal = ComputeGrandTotal();
        }
        finally
        {
            _recalculating = false;
        }
        UpdateChange();
    }

    /// <summary>
    /// Same rule as the server: cash rounding only when cash is paid, i.e. TUNAI, or KREDIT with a DP above zero.
    /// </summary>
    private decimal ComputeGrandTotal()
    {
        var afterDiscount = Total - DiscountAmount;
        var cashUsed = IsCash || (IsCredit && ReadDownPayment() is > 0);
        return cashUsed ? Rupiah.RoundCash(afterDiscount, Rounding.Step, Rounding.Mode).Total : afterDiscount;
    }

    /// <summary>KREDIT: the DP typed in the Bayar box; empty = 0, null = not a valid amount.</summary>
    private decimal? ReadDownPayment() =>
        BayarText.Trim().Length == 0 ? 0m : MoneyInput.TryParse(BayarText, out var dp) ? dp : null;

    /// <summary>"Kembali" for cash; "Sisa Piutang" (Grand Total - DP) for KREDIT.</summary>
    private void UpdateChange()
    {
        if (IsCredit)
        {
            GrandTotal = ComputeGrandTotal();
            Change = ReadDownPayment() is { } dp ? Math.Max(0m, GrandTotal - dp) : GrandTotal;
            return;
        }
        Change = IsCash && MoneyInput.TryParse(BayarText, out var bayar) && bayar > GrandTotal ? bayar - GrandTotal : 0m;
    }

    /// <summary>
    /// Enter on Bayar. Empty Bayar = "uang pas" (owner revision): Bayar becomes the Grand Total (Kembali 0) and the
    /// sale is saved at once. For KREDIT the box is the DP: empty = DP 0, the whole Grand Total becomes receivable.
    /// A typed amount that is too low still gets the message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task BayarEnterAsync()
    {
        if (BayarText.Trim().Length == 0 && !IsCredit)
            BayarText = Format(GrandTotal);
        return SaveAsync();
    }

    /// <summary>Enter on Keterangan or Referensi (and Bayar after <see cref="BayarEnterAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedMethod is not { } method)
        {
            ErrorMessage = "Pilih cara bayar.";
            Focus("method");
            return;
        }
        if (!TryReadDiscount(out var discount))
        {
            ErrorMessage = "Diskon tidak valid.";
            Focus("discount");
            return;
        }

        var paid = method.IsCredit ? CreditPayments(method) : SinglePayment(method);
        if (paid is null)
            return;

        try
        {
            SaleCalculator.Calculate(_cart.Lines.Select(l => l.SubTotal).ToList(), paid.Select(p => p.Input).ToList(), Rounding,
                _customer is not null, discount, _config.CurrencyDecimals);
        }
        catch (SaleValidationException ex)
        {
            ErrorMessage = ErrorMessages.For(ex.Code, 422, ex.Message, null);
            Focus(ex.Code is "invalid_discount" ? "discount" : "bayar");
            return;
        }

        SupervisorCredentials? supervisor = null;
        if (DiscountPolicy.RequiresApproval(DiscountAmount, Total, _config.DiscountMaxPercent, _config.CurrencyDecimals))
        {
            supervisor = _dialogs.AskSupervisor($"Diskon melebihi batas Anda (maks {_config.DiscountMaxPercent?.ToString("#,0.##", Indonesian) ?? "0"}%)");
            if (supervisor is null)
            {
                ErrorMessage = "Diskon ini perlu persetujuan supervisor.";
                Focus("discount");
                return;
            }
        }

        var reference = IsReferenceEnabled && !string.IsNullOrWhiteSpace(ReferenceText) ? ReferenceText.Trim() : null;
        var request = new CreateSaleRequest(
            SaleId,
            _customer?.Id,
            _cart.Lines.Select(l => new SaleLineRequest(l.Item.ProductUnitId, l.Qty)).ToList(),
            paid.Select(p => new SalePaymentRequest(p.Method.Code, p.Input.Amount, p.Method.IsCash || p.Method.IsCredit ? null : reference)).ToList(),
            string.IsNullOrWhiteSpace(NotesText) ? null : NotesText.Trim(),
            discount.PercentValue,
            discount.AmountValue,
            supervisor?.Username,
            supervisor?.Password);

        IsBusy = true;
        try
        {
            Result = await _api.CreateSaleAsync(request);
            DialogResult = true;
        }
        catch (NodeApiException ex) when (ex.Code is "node_unreachable")
        {
            ErrorMessage = $"{ex.Message} Tekan Enter lagi untuk mengirim ulang nota yang sama (tidak akan tercatat dua kali).";
            Focus("bayar");
        }
        catch (NodeApiException ex)
        {
            ErrorMessage = ex.Message;
            Focus("bayar");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy;

    private sealed record PaidWith(PaymentMethodInfo Method, PaymentInput Input);

    /// <summary>TUNAI / DEBIT / QRIS / TRANSFER: one payment of the typed amount (none when Bayar is empty).</summary>
    private List<PaidWith>? SinglePayment(PaymentMethodInfo method)
    {
        var bayar = 0m;
        if (BayarText.Trim().Length > 0 && !MoneyInput.TryParse(BayarText, out bayar))
        {
            ErrorMessage = "Jumlah bayar tidak valid.";
            Focus("bayar");
            return null;
        }
        var kind = method.IsCash ? PaymentKind.Cash : PaymentKind.NonCash;
        return bayar > 0 ? [new PaidWith(method, new PaymentInput(kind, bayar))] : [];
    }

    /// <summary>
    /// KREDIT: a Member is required; the DP (0..Grand Total) is paid as TUNAI and the rest is the receivable, so the
    /// server records exactly that rest as credit.
    /// </summary>
    private List<PaidWith>? CreditPayments(PaymentMethodInfo credit)
    {
        if (_customer is null)
        {
            ErrorMessage = "Pembayaran KREDIT wajib memilih Member. Pilih Member, lalu tekan Bayar [F6] lagi.";
            MemberRequired = true;
            DialogResult = false;
            return null;
        }
        if (ReadDownPayment() is not { } dp)
        {
            ErrorMessage = "Uang muka tidak valid.";
            Focus("bayar");
            return null;
        }

        var grandTotal = ComputeGrandTotal();
        if (dp > grandTotal)
        {
            ErrorMessage = "Uang muka tidak boleh melebihi Grand Total.";
            Focus("bayar");
            return null;
        }

        var payments = new List<PaidWith>();
        if (dp > 0)
        {
            if (_config.PaymentMethods.FirstOrDefault(m => m.IsCash) is not { } cash)
            {
                ErrorMessage = "Cara bayar TUNAI tidak tersedia untuk uang muka.";
                Focus("bayar");
                return null;
            }
            payments.Add(new PaidWith(cash, new PaymentInput(PaymentKind.Cash, dp)));
        }
        if (grandTotal - dp > 0)
            payments.Add(new PaidWith(credit, new PaymentInput(PaymentKind.Credit, grandTotal - dp)));
        return payments;
    }

    private void Focus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);

    private static string Format(decimal value) => value.ToString("#,0.####", Indonesian);
}
