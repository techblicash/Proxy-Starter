using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoLogService : IDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _cts;

    public event EventHandler<LogEntry>? LogReceived;

    public MihomoLogService(AppSettingsStore settingsStore)
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
                var settings = _settingsStore.Settings;
                var level = string.IsNullOrWhiteSpace(settings.LogLevel) ? "info" : settings.LogLevel;
                var query = $"level={Uri.EscapeDataString(level)}";
                if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
                {
                    query += $"&token={Uri.EscapeDataString(settings.ApiSecret)}";
                }

                var uri = new Uri($"ws://127.0.0.1:{settings.ApiPort}/logs?{query}");
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
                    if (TryParseLog(payload, out var entry))
                    {
                        LogReceived?.Invoke(this, entry);
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

    private static bool TryParseLog(string payload, out LogEntry entry)
    {
        entry = new LogEntry();

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var level = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "info";
            var message = root.TryGetProperty("payload", out var payloadElement) ? payloadElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            entry = new LogEntry
            {
                Level = (level ?? "info").ToUpperInvariant(),
                Message = message
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
