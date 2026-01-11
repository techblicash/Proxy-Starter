using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class ConnectionMonitorService : IDisposable
{
    private readonly MihomoApiClient _apiClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly System.Threading.Timer _timer;
    private bool _isRunning;

    public event EventHandler<ConnectionStatusSnapshot>? StatusUpdated;

    public ConnectionMonitorService(MihomoApiClient apiClient, AppSettingsStore settingsStore)
    {
        _apiClient = apiClient;
        _settingsStore = settingsStore;
        _timer = new System.Threading.Timer(OnTick);
    }

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        _isRunning = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    private async void OnTick(object? state)
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            var connections = await _apiClient.GetConnectionsAsync();
            var selectionGroup = _settingsStore.Settings.SelectionGroup;
            var activeNode = await _apiClient.GetSelectedProxyAsync(selectionGroup);

            var snapshot = new ConnectionStatusSnapshot(
                connections?.UploadTotal ?? 0,
                connections?.DownloadTotal ?? 0,
                activeNode ?? selectionGroup,
                connections?.CurrentAddress);

            StatusUpdated?.Invoke(this, snapshot);
        }
        catch
        {
        }
    }
}
