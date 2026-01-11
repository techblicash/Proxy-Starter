using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyStarter.App.Services;

public sealed class LatencyTestService
{
    private readonly MihomoApiClient _apiClient;

    public LatencyTestService(MihomoApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<int> TestTcpAsync(string host, int port, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            var watch = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            watch.Stop();
            return (int)watch.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public Task<int> TestProxyDelayAsync(string proxyName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        return _apiClient.TestDelayAsync(proxyName, timeoutMs, cancellationToken);
    }
}
