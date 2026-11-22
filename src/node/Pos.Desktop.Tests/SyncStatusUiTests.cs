using System.Windows.Controls;
using System.Windows.Media;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;
using static Pos.Desktop.Tests.Wpf;

namespace Pos.Desktop.Tests;

/// <summary>Status bar "Sinkron: …" in the real main window: text from the Node Service, red when it says so or is unreachable.</summary>
[Collection(WpfUiCollection.Name)]
public class SyncStatusUiTests
{
    private readonly FakeNodeApi _api = new();

    private MainViewModel Main(SyncStatusViewModel? sync)
    {
        var session = new SessionState();
        session.SetLogin(Samples.LoginWith("stock.view"));
        return new MainViewModel(session, new FormFactories(StockTotal: () => new StockTotalViewModel(_api, new FakeDialogs(),
            new MemoryPreferenceStore<StockTotalViewSettings>(new StockTotalViewSettings()), (_, _) => Task.CompletedTask)), sync: sync);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            DoEvents();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Shows_the_node_services_text_and_turns_red_when_it_is_late_or_unreachable() => OnSta(() =>
    {
        _api.OnSyncStatus = () => new SyncStatusInfo(true, "ok", DateTime.UtcNow, 0, 0, "Sinkron: 2 menit lalu · 0 tertunda", false);
        var sync = new SyncStatusViewModel(_api);
        var window = ShowOffScreen(new MainWindow { DataContext = Main(sync) });
        try
        {
            var text = Find<TextBlock>(window, "SyncStatusText");
            WaitFor(() => text.Text == "Sinkron: 2 menit lalu · 0 tertunda");      // loaded → refreshed

            Assert.Equal("Sinkron: 2 menit lalu · 0 tertunda", text.Text);
            Assert.True(text.IsVisible);
            Assert.NotEqual(Color.FromRgb(0xC6, 0x28, 0x28), (text.Foreground as SolidColorBrush)?.Color);

            _api.OnSyncStatus = () => new SyncStatusInfo(true, "offline", DateTime.UtcNow.AddMinutes(-40), 7, 0, "Sinkron: 40 menit lalu · 7 tertunda", true);
            sync.RefreshAsync().GetAwaiter().GetResult();
            DoEvents();
            Assert.Equal("Sinkron: 40 menit lalu · 7 tertunda", text.Text);
            Assert.Equal(Color.FromRgb(0xC6, 0x28, 0x28), ((SolidColorBrush)text.Foreground).Color);

            _api.OnSyncStatus = () => throw new NodeApiException("Server kasir tidak terjangkau.", null, null);
            sync.RefreshAsync().GetAwaiter().GetResult();
            DoEvents();
            Assert.Equal(SyncStatusViewModel.Unreachable, text.Text);
            Assert.Equal(Color.FromRgb(0xC6, 0x28, 0x28), ((SolidColorBrush)text.Foreground).Color);
        }
        finally
        {
            window.Close();
        }
        Assert.Contains("sync-status", _api.Calls);
    });

    [Fact]
    public void Is_hidden_without_a_sync_status() => OnSta(() =>
    {
        var window = ShowOffScreen(new MainWindow { DataContext = Main(null) });
        try
        {
            DoEvents();
            Assert.False(Find<TextBlock>(window, "SyncStatusText").IsVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
