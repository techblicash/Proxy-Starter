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
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private int _tickInProgress;

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
            var connections = await _apiClient.GetConnectionsAsync(token).ConfigureAwait(false);
            var settings = _settingsStore.Settings;
            var selectionGroup = RoutingDefaults.ResolveSelectionGroup(settings.SelectionGroup, settings.UseSubscriptionPolicyGroups);
            var activeNode = await _apiClient.GetResolvedSelectedProxyAsync(selectionGroup, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || !_isRunning)
            {
                return;
            }

            var snapshot = new ConnectionStatusSnapshot(
                connections?.UploadTotal ?? 0,
                connections?.DownloadTotal ?? 0,
                activeNode ?? selectionGroup,
                connections?.CurrentAddress);

            StatusUpdated?.Invoke(this, snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "ConnectionMonitorService: Tick");
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }
}
