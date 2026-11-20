using System.Globalization;
using System.Windows;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views.Dialogs;

namespace Pos.Desktop.Infrastructure;

/// <summary>Shows the sales screen's dialogs as modal WPF windows owned by the main window.</summary>
public sealed class WpfDialogService(INodeApi api) : IDialogService
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");

    public static readonly IReadOnlyList<PickerColumn> ProductColumns =
    [
        new("Kode Barang", nameof(ProductItem.Sku), 100),
        new("Nama Barang", nameof(ProductItem.Name), 0),
        new("Satuan", nameof(ProductItem.UnitCode), 70),
        new("Harga Jual", nameof(ProductItem.Price), 90, "money"),
        new("Stok", nameof(ProductItem.StockText), 120, Right: true),
        new("Merek", nameof(ProductItem.Brand), 100),
    ];

    public static readonly IReadOnlyList<PickerColumn> UnitColumns =
    [
        new("Satuan", nameof(ProductItem.UnitCode), 80),
        new("Nama Satuan", nameof(ProductItem.UnitName), 0),
        new("Isi", nameof(ProductItem.Conversion), 70, "qty"),
        new("Harga Jual", nameof(ProductItem.Price), 100, "money"),
    ];

    public static readonly IReadOnlyList<PickerColumn> CustomerColumns =
    [
        new("Kode", nameof(CustomerItem.Code), 110),
        new("Nama", nameof(CustomerItem.Name), 0),
        new("No HP", nameof(CustomerItem.Phone), 120),
        new("Sisa Limit", nameof(CustomerItem.CreditRemaining), 100, "money"),
        new("Poin", nameof(CustomerItem.Points), 60),
    ];

    public decimal? AskQuantity(string productName, decimal current)
    {
        var vm = new InputDialogViewModel("Atur Jumlah", $"Jumlah untuk {productName}:", current.ToString("#,0.####", Indonesian),
            text => MoneyInput.TryParse(text, out var q) && q > 0 ? null : "Masukkan jumlah lebih dari 0.");
        return Show(new InputDialog(), vm) && MoneyInput.TryParse(vm.Text, out var qty) ? qty : null;
    }

    public SupervisorCredentials? AskSupervisor(string purpose)
    {
        var vm = new SupervisorDialogViewModel(purpose);
        return Show(new SupervisorDialog(), vm) ? new SupervisorCredentials(vm.Username.Trim(), vm.Password) : null;
    }

    public string? AskCancelReason()
    {
        var vm = new InputDialogViewModel("Keterangan Batal Transaksi", "Keterangan pembatalan transaksi (wajib diisi):", "",
            text => text.Trim().Length == 0 ? "Keterangan wajib diisi." : null);
        return Show(new InputDialog(), vm) ? vm.Text.Trim() : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public ProductItem? PickProduct(string initialQuery)
    {
        var vm = new PickerDialogViewModel("Cari Barang [F1]", ProductColumns,
            search: async (q, ct) => (await api.SearchProductsAsync(q, ct)).Cast<object>().ToList(), initialQuery: initialQuery);
        return Show(new PickerDialog(), vm) ? vm.Result as ProductItem : null;
    }

    public ProductItem? PickUnit(string productName, IReadOnlyList<ProductItem> units, Guid currentUnitId)
    {
        var vm = new PickerDialogViewModel($"Ganti Satuan - {productName}", UnitColumns, units)
        {
            SelectedItem = units.FirstOrDefault(u => u.ProductUnitId != currentUnitId) ?? units.FirstOrDefault(),
        };
        return Show(new PickerDialog(), vm) ? vm.Result as ProductItem : null;
    }

    public CustomerItem? PickCustomer(string initialQuery)
    {
        var vm = new PickerDialogViewModel("Cari Member", CustomerColumns,
            search: async (q, ct) => (await api.SearchCustomersAsync(q, ct)).Cast<object>().ToList(), initialQuery: initialQuery);
        return Show(new PickerDialog(), vm) ? vm.Result as CustomerItem : null;
    }

    public SaleResult? ShowPayment(PaymentViewModel payment) =>
        Show(new PaymentDialog(), payment) ? payment.Result : null;

    public void ShowSaleResult(SaleResult result) => Show(new SaleResultDialog(), new SaleResultViewModel(result));

    public void ShowReceiptPreview(string message, IReadOnlyList<string> lines) =>
        Show(new ReceiptPreviewDialog(), new ReceiptPreviewViewModel(message, lines));

    private static bool Show(Window window, object viewModel)
    {
        window.DataContext = viewModel;
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
            window.Owner = owner;
        return window.ShowDialog() == true;
    }
}
