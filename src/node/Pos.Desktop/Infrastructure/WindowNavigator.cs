using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;

namespace Pos.Desktop.Infrastructure;

/// <summary>Shows one top-level window at a time; the new window becomes the application's main window.</summary>
public sealed class WindowNavigator(IServiceProvider services) : INavigator
{
    public void ShowLogin() => Show<LoginWindow, LoginViewModel>();

    public void ShowOpenShift() => Show<OpenShiftWindow, OpenShiftViewModel>();

    public void ShowMain() => Show<MainWindow, MainViewModel>();

    private void Show<TWindow, TViewModel>()
        where TWindow : Window
        where TViewModel : class
    {
        var window = services.GetRequiredService<TWindow>();
        window.DataContext = services.GetRequiredService<TViewModel>();

        var previous = Application.Current.MainWindow;
        Application.Current.MainWindow = window;
        window.Show();
        if (previous is not null && !ReferenceEquals(previous, window))
            previous.Close();
    }
}
