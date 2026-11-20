using System.Windows;
using System.Windows.Input;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Views.Dialogs;

/// <summary>View only; behaviour lives in PickerDialogViewModel.</summary>
public partial class PickerDialog : Window
{
    public PickerDialog() => InitializeComponent();

    /// <summary>Initial focus only: the search box when searchable, otherwise the list.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PickerDialogViewModel { IsSearchable: true })
            Keyboard.Focus(QueryBox);
        else
            Keyboard.Focus(ItemsGrid);
    }
}
