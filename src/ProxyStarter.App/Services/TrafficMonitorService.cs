using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class TrafficMonitorService : IDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _lastEmitAtMs;
    private const int MinEmitIntervalMs = 200;

    public event EventHandler<TrafficSnapshot>? TrafficUpdated;

    public TrafficMonitorService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_cts is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _runTask = RunAsync(cts.Token).ContinueWith(
                _ => cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts = null;
        }
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
                var settings = _settingsStore.Settings;
                ApplyAuthorization(socket, settings.ApiSecret);
                var uri = new Uri($"ws://127.0.0.1:{settings.ApiPort}/traffic");
                await socket.ConnectAsync(uri, cancellationToken);

                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    // Mihomo emits traffic very frequently; throttle to reduce CPU/GPU/UI churn.
                    var nowMs = Environment.TickCount64;
                    if (nowMs - _lastEmitAtMs < MinEmitIntervalMs)
                    {
                        continue;
                    }

                    _lastEmitAtMs = nowMs;

                    if (TryParseTraffic(buffer.AsMemory(0, result.Count), out var up, out var down))
                    {
                        TrafficUpdated?.Invoke(this, new TrafficSnapshot(up, down, 0, 0));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsExpectedWebSocketFailure(ex))
            {
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "TrafficMonitorService: WebSocket");
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

    private static void ApplyAuthorization(ClientWebSocket socket, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        try
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {secret}");
        }
        catch
        {
        }
    }

    private static bool IsExpectedWebSocketFailure(Exception exception)
    {
        return exception is WebSocketException
            or HttpRequestException
            or IOException
            or SocketException
            or TaskCanceledException;
    }

    private static bool TryParseTraffic(ReadOnlyMemory<byte> payload, out long up, out long down)
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
