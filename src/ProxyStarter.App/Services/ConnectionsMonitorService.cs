using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class ConnectionsMonitorService : IDisposable
{
    private readonly MihomoApiClient _apiClient;
    private readonly System.Threading.Timer _timer;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private int _tickInProgress;

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
        _cts = new CancellationTokenSource();
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _timer.Dispose();
    }

    private void OnTick(object? state)
    {
        _ = OnTickAsync();
    }

    private async Task OnTickAsync()
    {
        if (!_isRunning || Interlocked.Exchange(ref _tickInProgress, 1) == 1)
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        try
        {
            var snapshot = await _apiClient.GetConnectionsAsync(token).ConfigureAwait(false);
            if (snapshot is not null && !token.IsCancellationRequested && _isRunning)
            {
                SnapshotUpdated?.Invoke(this, snapshot);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "ConnectionsMonitorService: Tick");
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }
}
