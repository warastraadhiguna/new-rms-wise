using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using Pos.Desktop.Views.Dialogs;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Dialogs and the main window driven through their real controls.</summary>
[Collection(WpfUiCollection.Name)]
public class DialogUiTests
{
    [Fact]
    public void Input_dialog_validates_then_accepts_with_enter() => OnSta(() =>
    {
        var vm = new InputDialogViewModel("Keterangan Batal Transaksi", "Keterangan:", "",
            text => text.Trim().Length == 0 ? "Keterangan wajib diisi." : null);
        var window = ShowOffScreen(new InputDialog { DataContext = vm });

        var box = Find<TextBox>(window, "ValueBox");
        PressKey(box, Key.Enter);
        Assert.Equal("Keterangan wajib diisi.", Find<TextBlock>(window, "ErrorText").Text);
        Assert.True(window.IsVisible);

        box.Text = "Pelanggan batal membeli";
        PressKey(box, Key.Enter);

        Assert.True(vm.DialogResult);
        Assert.Equal("Pelanggan batal membeli", vm.Text);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void Receipt_preview_shows_the_message_and_the_receipt_in_a_monospace_box_and_enter_closes() => OnSta(() =>
    {
        var lines = Pos.Shared.Printing.ReceiptFormatter.Format(
            Pos.Desktop.Services.ReceiptService.ToReceiptData(Receipts.Detail(Guid.CreateVersion7())), 48);
        var vm = new ReceiptPreviewViewModel("Printer struk belum diatur.", lines);
        var window = ShowOffScreen(new ReceiptPreviewDialog { DataContext = vm });

        Assert.Equal("Printer struk belum diatur.", Find<TextBlock>(window, "MessageText").Text);
        var box = Find<TextBox>(window, "ReceiptBox");
        Assert.Equal(string.Join(Environment.NewLine, lines), box.Text);
        Assert.True(box.IsReadOnly);
        Assert.Equal(TextWrapping.NoWrap, box.TextWrapping);
        Assert.StartsWith("Consolas", box.FontFamily.Source);
        Assert.Equal(48, vm.Columns);
        // Every receipt character is equally wide: the dashed rule and the widest text line measure the same.
        Assert.Equal(Measure(box, new string('-', 48)), Measure(box, new string('W', 48)), precision: 1);

        PressKey(Find<Button>(window, "CloseButton"), Key.Enter);

        Assert.True(vm.DialogResult);
        Assert.False(window.IsVisible);
    });

    private static double Measure(TextBox box, string text) => new System.Windows.Media.FormattedText(
        text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize,
        System.Windows.Media.Brushes.Black, 1.0).Width;

    [Fact]
    public void Receipt_preview_escape_closes_too() => OnSta(() =>
    {
        var vm = new ReceiptPreviewViewModel("Gagal mencetak.", ["A", "B"]);
        var window = ShowOffScreen(new ReceiptPreviewDialog { DataContext = vm });

        PressKey(Find<TextBox>(window, "ReceiptBox"), Key.Escape);

        Assert.False(window.IsVisible);
    });

    [Fact]
    public void Input_dialog_escape_cancels() => OnSta(() =>
    {
        var vm = new InputDialogViewModel("Atur Jumlah", "Jumlah:", "1", _ => null);
        var window = ShowOffScreen(new InputDialog { DataContext = vm });

        PressKey(Find<TextBox>(window, "ValueBox"), Key.Escape);

        Assert.False(vm.DialogResult);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void Supervisor_dialog_reads_username_and_password_from_the_controls() => OnSta(() =>
    {
        var vm = new SupervisorDialogViewModel("Hapus barang dari keranjang");
        var window = ShowOffScreen(new SupervisorDialog { DataContext = vm });
        Assert.Same(Find<TextBox>(window, "UsernameBox"), FocusManager.GetFocusedElement(window));

        Find<TextBox>(window, "UsernameBox").Text = "spv";
        PressKey(Find<TextBox>(window, "UsernameBox"), Key.Enter);
        Assert.Equal("Isi password supervisor.", Find<TextBlock>(window, "ErrorText").Text);

        var password = Find<PasswordBox>(window, "PasswordInput");
        password.Password = "rahasia";
        PressKey(password, Key.Enter);

        Assert.True(vm.DialogResult);
        Assert.Equal("spv", vm.Username);
        Assert.Equal("rahasia", vm.Password);
    });

    [Fact]
    public void Picker_dialog_searches_while_typing_and_enter_chooses() => OnSta(() =>
    {
        var vm = new PickerDialogViewModel("Cari Barang [F1]", Pos.Desktop.Infrastructure.WpfDialogService.ProductColumns,
            search: (q, _) => Task.FromResult<IReadOnlyList<object>>(
                new[] { Samples.Amplop, Samples.Kantong }.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList()),
            delay: (_, _) => Task.CompletedTask);
        var window = ShowOffScreen(new PickerDialog { DataContext = vm });
        var query = Find<TextBox>(window, "QueryBox");
        var grid = Find<DataGrid>(window, "ItemsGrid");
        Assert.Equal(["Kode Barang", "Nama Barang", "Satuan", "Harga Jual", "Stok", "Merek"], grid.Columns.Select(c => (string)c.Header));

        query.Text = "kantong";
        DoEvents();
        Assert.Single(grid.Items);

        PressKey(query, Key.Enter);

        Assert.True(vm.DialogResult);
        Assert.Same(Samples.Kantong, vm.Result);
    });

    [Fact]
    public void Picker_dialog_without_search_lists_items_and_enter_on_the_grid_chooses() => OnSta(() =>
    {
        var vm = new PickerDialogViewModel("Ganti Satuan", Pos.Desktop.Infrastructure.WpfDialogService.UnitColumns,
            [Samples.Amplop, Samples.AmplopPak]);
        var window = ShowOffScreen(new PickerDialog { DataContext = vm });
        var grid = Find<DataGrid>(window, "ItemsGrid");
        Assert.Equal(Visibility.Collapsed, Find<TextBox>(window, "QueryBox").IsVisible ? Visibility.Visible : Visibility.Collapsed);

        grid.SelectedIndex = 1;
        PressKey(grid, Key.Enter);

        Assert.Same(Samples.AmplopPak, vm.Result);
    });

    [Fact]
    public void Main_window_hosts_the_sales_form_and_f2_reopens_it() => OnSta(() =>
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("sale.create"));
        session.Shift = Samples.Shift();
        var vm = new MainViewModel(session, () => new SalesViewModel(new FakeNodeApi(), session, new FakeDialogs(),
            new MemoryCartStore(), new MemoryViewSettingsStore(), Receipts.Service(new FakeNodeApi(), new FakeDialogs()), (_, _) => Task.CompletedTask));
        var window = ShowOffScreen(new MainWindow { DataContext = vm });
        try
        {
            Assert.NotNull(vm.SalesForm);
            Find<TabControl>(window, "FormTabs").UpdateLayout();
            Click(VisualTreeSearch.Descendants<Button>(window).Single(b => b.Name == "CloseTabButton"));   // empty cart: closes without asking
            Assert.Null(vm.SalesForm);
            Assert.True(Find<Button>(window, "OpenSalesButton").IsVisible);

            PressKey(Find<Button>(window, "OpenSalesButton"), Key.F2);
            Assert.NotNull(vm.SalesForm);
        }
        finally
        {
            window.Close();
        }
    });
}
