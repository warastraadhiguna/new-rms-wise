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
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<ICartStore>(_ => new FileCartStore(FileCartStore.PathFor(settings.DeviceId)));
        services.AddSingleton<IViewSettingsStore>(_ => new FileViewSettingsStore(FileViewSettingsStore.DefaultPath));
        services.AddSingleton<IReceiptPrinter, WindowsRawPrinter>();
        services.AddSingleton<ILastSaleStore>(_ => new FileLastSaleStore(FileLastSaleStore.PathFor(settings.DeviceId)));
        services.AddSingleton(new ReceiptPrinterSettings(settings.PrinterName, settings.ReceiptWidth));
        services.AddSingleton<ReceiptService>();
        services.AddTransient(sp => new SalesViewModel(
            sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<SessionState>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ICartStore>(), sp.GetRequiredService<IViewSettingsStore>(), sp.GetRequiredService<ReceiptService>()));
        services.AddTransient<Func<SalesViewModel>>(sp => () => sp.GetRequiredService<SalesViewModel>());
        services.AddSingleton<IPreferenceStore<StockTotalViewSettings>>(_ =>
            new FilePreferenceStore<StockTotalViewSettings>("stock-total-view.json", () => new StockTotalViewSettings()));
        services.AddTransient(sp => new StockTotalViewModel(
            sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<IPreferenceStore<StockTotalViewSettings>>()));
        services.AddTransient(sp => new StockCheckViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>()));
        services.AddTransient(sp => new StockCardViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>()));
        services.AddTransient(sp => new LocationTransferViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>()));
        services.AddTransient(sp => new LocationTransferListViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>()));
        services.AddTransient(sp => new MainViewModel(sp.GetRequiredService<SessionState>(), new FormFactories(
            Sales: sp.GetRequiredService<Func<SalesViewModel>>(),
            StockTotal: () => sp.GetRequiredService<StockTotalViewModel>(),
            StockCheck: () => sp.GetRequiredService<StockCheckViewModel>(),
            StockCard: () => sp.GetRequiredService<StockCardViewModel>(),
            LocationTransfer: () => sp.GetRequiredService<LocationTransferViewModel>(),
            LocationTransferList: () => sp.GetRequiredService<LocationTransferListViewModel>(),
            CloseShift: shift => new CloseShiftViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<ReceiptService>(), shift, sp.GetRequiredService<SessionState>().User?.Id ?? Guid.Empty),
            SalesList: () => new SalesListViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<ReceiptService>(), sp.GetRequiredService<SessionState>()),
            ReturnForm: () => new ReturnFormViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<ReceiptService>(), sp.GetRequiredService<SessionState>()),
            ReturnList: () => new ReturnListViewModel(sp.GetRequiredService<INodeApi>(), sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<ReceiptService>())),
            sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<INavigator>()));
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
