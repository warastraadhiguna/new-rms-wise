using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pos.Desktop.ViewModels;

/// <summary>Base for modal dialogs: <c>DialogResult</c> is bound to the window through DialogCloser.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    [ObservableProperty] private bool? _dialogResult;
    [ObservableProperty] private string? _errorMessage;

    [RelayCommand]
    private void Cancel() => DialogResult = false;
}

/// <summary>One text value with validation (Atur Jumlah, Keterangan Batal Transaksi).</summary>
/// <param name="validate">Returns an error text, or null when the value is acceptable.</param>
public sealed partial class InputDialogViewModel(string title, string prompt, string initialText, Func<string, string?> validate)
    : DialogViewModel
{
    public string Title { get; } = title;
    public string Prompt { get; } = prompt;

    [ObservableProperty] private string _text = initialText;

    [RelayCommand]
    private void Ok()
    {
        ErrorMessage = validate(Text);
        if (ErrorMessage is null)
            DialogResult = true;
    }
}

/// <summary>Supervisor username + password (RMS 2 asks the supervisor's password).</summary>
public sealed partial class SupervisorDialogViewModel(string purpose) : DialogViewModel
{
    public string Purpose { get; } = purpose;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    [RelayCommand]
    private void Ok()
    {
        ErrorMessage = Username.Trim().Length == 0 ? "Isi username supervisor."
            : Password.Length == 0 ? "Isi password supervisor."
            : null;
        if (ErrorMessage is null)
            DialogResult = true;
    }
}

/// <summary>"Transaksi Berhasil": nota number and change, large, closed with Enter.</summary>
public sealed partial class SaleResultViewModel(Services.SaleResult result) : DialogViewModel
{
    public string Number { get; } = result.Sale.Number;
    public decimal Total { get; } = result.Sale.Total;
    public decimal Change { get; } = result.Sale.Change;
    public string Warnings { get; } = string.Join(" ", result.Warnings.Select(w =>
        w.Code == "credit_limit_exceeded" ? "Kredit pelanggan melebihi limit." : w.Message));

    [RelayCommand]
    private void Close() => DialogResult = true;
}

/// <param name="Path">Property of the item shown in this column.</param>
/// <param name="Format">"money" or "qty" for Indonesian number formatting, null for text.</param>
public sealed record PickerColumn(string Header, string Path, double Width, string? Format = null);

/// <summary>
/// A list to pick from, optionally searchable: F1 product search, unit choice, member search.
/// Enter (in the search box or the grid) or double-click takes the selected row.
/// </summary>
public sealed partial class PickerDialogViewModel : DialogViewModel
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<object>>>? _search;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cts;

    public PickerDialogViewModel(
        string title, IReadOnlyList<PickerColumn> columns, IEnumerable<object>? items = null,
        Func<string, CancellationToken, Task<IReadOnlyList<object>>>? search = null, string initialQuery = "",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Title = title;
        Columns = columns;
        _search = search;
        _delay = delay ?? Task.Delay;
        foreach (var item in items ?? [])
            Items.Add(item);
        SelectedItem = Items.FirstOrDefault();
        _query = initialQuery;
        if (search is not null && initialQuery.Trim().Length > 0)
            PendingSearch = RunSearchAsync(initialQuery.Trim(), CancellationToken.None, debounce: false);
    }

    public string Title { get; }
    public IReadOnlyList<PickerColumn> Columns { get; }
    public bool IsSearchable => _search is not null;
    public ObservableCollection<object> Items { get; } = [];
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private string _query;
    [ObservableProperty] private object? _selectedItem;

    public object? Result { get; private set; }

    partial void OnQueryChanged(string value)
    {
        if (_search is null)
            return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        PendingSearch = RunSearchAsync(value.Trim(), _cts.Token, debounce: true);
    }

    private async Task RunSearchAsync(string text, CancellationToken token, bool debounce)
    {
        try
        {
            if (debounce)
                await _delay(SalesViewModel.SearchDelay, token);
            if (token.IsCancellationRequested)
                return;
            var found = text.Length == 0 ? [] : await _search!(text, token);
            if (token.IsCancellationRequested)
                return;
            Items.Clear();
            foreach (var item in found)
                Items.Add(item);
            SelectedItem = Items.FirstOrDefault();
            ErrorMessage = found.Count == 0 && text.Length > 0 ? "Tidak ditemukan." : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Services.NodeApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Choose()
    {
        if (SelectedItem is null)
        {
            ErrorMessage = "Pilih salah satu baris.";
            return;
        }
        Result = SelectedItem;
        DialogResult = true;
    }
}
