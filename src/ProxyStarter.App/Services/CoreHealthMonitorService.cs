using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyStarter.App.Services;

public sealed class CoreHealthMonitorService : IDisposable
{
    private static readonly Uri[] ProbeUrls =
    {
        new("http://www.gstatic.com/generate_204"),
        new("http://cp.cloudflare.com/generate_204"),
        new("http://www.msftconnecttest.com/connecttest.txt")
    };

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(2);
    private const int FailureThreshold = 3;

    private readonly CoreController _coreController;
    private readonly AppSettingsStore _settingsStore;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private int _tickInProgress;
    private int _consecutiveFailures;
    private DateTimeOffset _lastRestartAt = DateTimeOffset.MinValue;

    public CoreHealthMonitorService(CoreController coreController, AppSettingsStore settingsStore)
    {
        _coreController = coreController;
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
        _timer.Change(TimeSpan.FromSeconds(15), ProbeInterval);
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
        _restartGate.Dispose();
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
            if (!_coreController.IsRunning)
            {
                _consecutiveFailures = 0;
                return;
            }

            var probePort = ResolveProbePort();
            if (probePort <= 0)
            {
                _consecutiveFailures = 0;
                return;
            }

            if (await ProbeProxyAsync(probePort, token).ConfigureAwait(false))
            {
                _consecutiveFailures = 0;
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures < FailureThreshold)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - _lastRestartAt < RestartCooldown)
            {
                return;
            }

            await RestartCoreAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "CoreHealthMonitorService: Tick");
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    private int ResolveProbePort()
    {
        var settings = _settingsStore.Settings;
        if (settings.MixedPort > 0)
        {
            return settings.MixedPort;
        }

        return settings.HttpPort > 0 ? settings.HttpPort : 0;
    }

    private static async Task<bool> ProbeProxyAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        foreach (var url in ProbeUrls)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                    UseProxy = true,
                    AutomaticDecompression = DecompressionMethods.All
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = ProbeTimeout
                };

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 200 and < 400)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return false;
    }

    private async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        await _restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_coreController.IsRunning)
            {
                _consecutiveFailures = 0;
                return;
            }

            _lastRestartAt = DateTimeOffset.UtcNow;
            CrashLogger.Log(
                new InvalidOperationException($"Proxy health probe failed {_consecutiveFailures} consecutive times; restarting core."),
                "CoreHealthMonitorService: Restart core");

            await _coreController.StopAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await _coreController.StartAsync(cancellationToken).ConfigureAwait(false);
            _consecutiveFailures = 0;
        }
        finally
        {
            _restartGate.Release();
        }
    }
}
