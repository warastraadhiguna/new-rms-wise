using CommunityToolkit.Mvvm.ComponentModel;
using Pos.Desktop.Services;

namespace Pos.Desktop.ViewModels;

/// <summary>
/// Status bar "Sinkron: 2 menit lalu · 0 tertunda", red when the Node Service says so (no success within
/// sync.alert_minutes, or refused documents) or when the Node Service itself cannot be reached. Refreshed every
/// <see cref="Interval"/> while the main window is open; the text comes from the Node Service (its clock, not this PC's).
/// </summary>
public sealed partial class SyncStatusViewModel(INodeApi api) : ObservableObject, IDisposable
{
    public const string Unreachable = "Sinkron: Node Service tidak terjangkau";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _loop;

    [ObservableProperty] private string _text = "Sinkron: …";

    [ObservableProperty] private bool _isAlert;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await api.GetSyncStatusAsync(ct);
            Text = status.Text;
            IsAlert = status.IsAlert;
        }
        catch (NodeApiException)
        {
            Text = Unreachable;
            IsAlert = true;
        }
    }

    /// <summary>Refresh now and then every <see cref="Interval"/> until <see cref="Stop"/>.</summary>
    public void Start()
    {
        if (_loop is not null)
            return;
        _loop = new CancellationTokenSource();
        _ = LoopAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
    }
}
