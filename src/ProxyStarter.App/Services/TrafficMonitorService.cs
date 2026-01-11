using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class TrafficMonitorService : IDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _cts;

    public event EventHandler<TrafficSnapshot>? TrafficUpdated;

    public TrafficMonitorService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{_settingsStore.Settings.ApiPort}/traffic");
                await socket.ConnectAsync(uri, cancellationToken);

                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (TryParseTraffic(payload, out var up, out var down))
                    {
                        TrafficUpdated?.Invoke(this, new TrafficSnapshot(up, down, 0, 0));
                    }
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                return;
            }
        }
    }

    private static bool TryParseTraffic(string payload, out long up, out long down)
    {
        up = 0;
        down = 0;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("up", out var upElement))
            {
                up = upElement.GetInt64();
            }
            if (root.TryGetProperty("down", out var downElement))
            {
                down = downElement.GetInt64();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
