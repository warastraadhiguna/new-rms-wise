using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pos.Desktop.Services;
using Pos.Shared.Auth;
using Pos.Shared.Money;
using Pos.Shared.Sales;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// F6 "Bayar", like RMS 2 BayarNotaForm: Grand Total, Cara Bayar, Diskon (%) / Potongan (Rp), Total, Bayar,
/// Kembali, Referensi, Keterangan. Totals use the same Pos.Shared rules as the server (header discount,
/// cash rounding only for cash). The sale id is fixed for this cart, so a retry after a network failure
/// cannot create a second sale.
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

    public PaymentViewModel(INodeApi api, IDialogService dialogs, SalesConfig config, Cart cart, CustomerItem? customer, Guid saleId)
    {
        _api = api;
        _dialogs = dialogs;
        _config = config;
        _cart = cart;
        _customer = customer;
        SaleId = saleId;
        GrandTotal = cart.Total;
        _selectedMethod = config.PaymentMethods.FirstOrDefault(m => m.IsCash) ?? config.PaymentMethods.FirstOrDefault();
        Recalculate();
        FocusRequest = new FocusRequest("method", ++_focusSequence);
    }

    public Guid SaleId { get; }
    public decimal GrandTotal { get; }
    public IReadOnlyList<PaymentMethodInfo> PaymentMethods => _config.PaymentMethods;
    public IReadOnlyList<string> DiscountModes { get; } = ["Diskon (%)", "Potongan (Rp)"];
    public string? CustomerName => _customer?.Display;
    public SaleResult? Result { get; private set; }

    [ObservableProperty] private PaymentMethodInfo? _selectedMethod;
    [ObservableProperty] private bool _isNominalDiscount;
    [ObservableProperty] private string _discountText = "0";
    [ObservableProperty] private decimal _discountAmount;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private string _bayarText = "";
    [ObservableProperty] private decimal _change;
    [ObservableProperty] private string _referenceText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private FocusRequest? _focusRequest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>Index for the "Diskon (%) / Potongan (Rp)" combo box.</summary>
    public int DiscountModeIndex
    {
        get => IsNominalDiscount ? 1 : 0;
        set => IsNominalDiscount = value == 1;
    }

    partial void OnSelectedMethodChanged(PaymentMethodInfo? value)
    {
        _bayarEdited = false;
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

    private bool _recalculating;

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
                DiscountAmount = SaleCalculator.HeaderDiscountAmount(discount, GrandTotal, _config.CurrencyDecimals);
            }
            catch (SaleValidationException)
            {
                ErrorMessage = "Diskon tidak valid.";
                DiscountAmount = 0m;
            }

            var afterDiscount = GrandTotal - DiscountAmount;
            Total = IsCash ? Rupiah.RoundCash(afterDiscount, Rounding.Step, Rounding.Mode).Total : afterDiscount;

            // Non-cash pays exactly the total (RMS 2: Bayar = Total); cash is typed by the cashier.
            if (!IsCash)
                BayarText = Format(Total);
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
        Change = IsCash && MoneyInput.TryParse(BayarText, out var bayar) && bayar > Total ? bayar - Total : 0m;

    /// <summary>Enter on the last field (Keterangan) or the Simpan button.</summary>
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
        if (DiscountPolicy.RequiresApproval(DiscountAmount, GrandTotal, _config.DiscountMaxPercent, _config.CurrencyDecimals))
        {
            supervisor = _dialogs.AskSupervisor($"Diskon melebihi batas Anda (maks {_config.DiscountMaxPercent?.ToString("#,0.##", Indonesian) ?? "0"}%)");
            if (supervisor is null)
            {
                ErrorMessage = "Diskon ini perlu persetujuan supervisor.";
                Focus("discount");
                return;
            }
        }

        var request = new CreateSaleRequest(
            SaleId,
            _customer?.Id,
            _cart.Lines.Select(l => new SaleLineRequest(l.Item.ProductUnitId, l.Qty)).ToList(),
            payments.Length == 0 ? [] : [new SalePaymentRequest(method.Code, bayar, string.IsNullOrWhiteSpace(ReferenceText) ? null : ReferenceText.Trim())],
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
            ErrorMessage = $"{ex.Message} Tekan Simpan lagi untuk mengirim ulang nota yang sama (tidak akan tercatat dua kali).";
        }
        catch (NodeApiException ex)
        {
            ErrorMessage = ex.Message;
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
