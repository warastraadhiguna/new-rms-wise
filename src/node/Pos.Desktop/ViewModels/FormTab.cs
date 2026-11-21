using CommunityToolkit.Mvvm.ComponentModel;

namespace Pos.Desktop.ViewModels;

/// <summary>A form shown as a tab in the main window ("Form Penjualan 1", "Form Stok Total", ...).</summary>
public abstract class FormTab : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Raised when the form wants to close (Esc, the tab's ×).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The tab's ×: a form may ask first (the sales form does when the cart is not empty).</summary>
    public virtual void RequestClose() => RaiseCloseRequested();

    protected void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
