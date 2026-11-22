using System.Windows;
using Pos.Desktop.ViewModels;

namespace Pos.Desktop.Views;

/// <summary>View only; behaviour lives in MainViewModel. The sync status refreshes while the window is open.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.Sync?.Start();
        Closed += (_, _) => (DataContext as MainViewModel)?.Sync?.Stop();
    }
}
