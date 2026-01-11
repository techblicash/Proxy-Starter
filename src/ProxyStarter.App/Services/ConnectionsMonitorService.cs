using System;
using System.Threading;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class ConnectionsMonitorService : IDisposable
{
    private readonly MihomoApiClient _apiClient;
    private readonly System.Threading.Timer _timer;
    private bool _isRunning;

    public event EventHandler<MihomoConnectionSnapshot>? SnapshotUpdated;

    public ConnectionsMonitorService(MihomoApiClient apiClient)
    {
        _apiClient = apiClient;
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
            var snapshot = await _apiClient.GetConnectionsAsync();
            if (snapshot is not null)
            {
                SnapshotUpdated?.Invoke(this, snapshot);
            }
        }
        catch
        {
        }
    }
}
