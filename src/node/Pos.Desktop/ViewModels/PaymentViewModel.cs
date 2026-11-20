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

            var afterDiscount = Total - DiscountAmount;
            GrandTotal = IsCash ? Rupiah.RoundCash(afterDiscount, Rounding.Step, Rounding.Mode).Total : afterDiscount;

            // Non-cash pays exactly the grand total (RMS 2); cash is typed by the cashier.
            if (!IsCash)
                BayarText = Format(GrandTotal);
            else if (!_bayarEdited)
                BayarText = "";
        }
        finally
        {
            _recalculating = false;
        }
        UpdateChange();
    }

    private void UpdateChange() =>
        Change = IsCash && MoneyInput.TryParse(BayarText, out var bayar) && bayar > GrandTotal ? bayar - GrandTotal : 0m;

    /// <summary>
    /// Enter on Bayar. Empty Bayar = "uang pas" (owner revision): Bayar becomes the Grand Total (Kembali 0) and the
    /// sale is saved at once, for every payment method. A typed amount that is too low still gets the message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task BayarEnterAsync()
    {
        if (BayarText.Trim().Length == 0)
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

        var bayar = 0m;
        if (BayarText.Trim().Length > 0 && !MoneyInput.TryParse(BayarText, out bayar))
        {
            ErrorMessage = "Jumlah bayar tidak valid.";
            Focus("bayar");
            return;
        }

        var kind = method.IsCash ? PaymentKind.Cash : method.IsCredit ? PaymentKind.Credit : PaymentKind.NonCash;
        PaymentInput[] payments = bayar > 0 ? [new PaymentInput(kind, bayar)] : [];
        try
        {
            SaleCalculator.Calculate(_cart.Lines.Select(l => l.SubTotal).ToList(), payments, Rounding, _customer is not null,
                discount, _config.CurrencyDecimals);
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
            payments.Length == 0 ? [] : [new SalePaymentRequest(method.Code, bayar, reference)],
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

    private void Focus(string target) => FocusRequest = new FocusRequest(target, ++_focusSequence);

    private static string Format(decimal value) => value.ToString("#,0.####", Indonesian);
}
