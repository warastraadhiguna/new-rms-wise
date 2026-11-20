using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pos.Desktop.Configuration;
using Pos.Desktop.Infrastructure;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;

namespace Pos.Desktop;

/// <summary>Composition root: settings, dependency injection, first window.</summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var loaded = new DesktopSettingsStore(DesktopSettingsStore.DefaultPath).Load();
        if (loaded.Settings is not { } settings)
        {
            MessageBox.Show(loaded.Error, "Konfigurasi Kasir", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        var builder = Host.CreateApplicationBuilder();
        var services = builder.Services;
        services.AddSingleton(settings);
        services.AddSingleton<SessionState>();
        services.AddSingleton(_ => new HttpClient
        {
            BaseAddress = new Uri(settings.NodeServiceUrl),
            Timeout = TimeSpan.FromSeconds(15),
        });
        services.AddSingleton<INodeApi, NodeApiClient>();
        services.AddSingleton<INavigator, WindowNavigator>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<OpenShiftViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<LoginWindow>();
        services.AddTransient<OpenShiftWindow>();
        services.AddTransient<MainWindow>();

        _host = builder.Build();
        _host.Start();
        _host.Services.GetRequiredService<INavigator>().ShowLogin();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Terjadi kesalahan yang tidak terduga:\n{e.Exception.Message}\n\nAplikasi tetap berjalan. Jika berulang, hubungi admin.",
            "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
