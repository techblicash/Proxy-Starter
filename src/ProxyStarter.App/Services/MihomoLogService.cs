using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoLogService : IDisposable
{
    public const int MaxRetentionLimit = 2000;

    private readonly AppSettingsStore _settingsStore;
    private readonly object _syncRoot = new();
    private readonly object _runSync = new();
    private readonly Queue<LogEntry> _entries = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public event EventHandler<LogEntry>? LogReceived;

    public int RetentionLimit => GetRetentionLimit();

    public MihomoLogService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Start()
    {
        lock (_runSync)
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
        lock (_runSync)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_syncRoot)
        {
            TrimEntries();
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
        }
    }

    public void TrimToRetention()
    {
        lock (_syncRoot)
        {
            TrimEntries();
        }
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

                    if (TryParseLog(buffer.AsMemory(0, result.Count), out var entry))
                    {
                        AddEntry(entry);
                        LogReceived?.Invoke(this, entry);
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
                CrashLogger.Log(ex, "MihomoLogService: WebSocket");
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

    private void AddEntry(LogEntry entry)
    {
        lock (_syncRoot)
        {
            _entries.Enqueue(entry);
            TrimEntries();
        }
    }

    private void TrimEntries()
    {
        var maxEntries = GetRetentionLimit();
        while (_entries.Count > maxEntries)
        {
            _entries.Dequeue();
        }
    }

    private int GetRetentionLimit()
    {
        var retention = _settingsStore.Settings.LogRetentionCount;
        if (retention <= 0)
        {
            retention = 1;
        }

        return Math.Clamp(retention, 1, MaxRetentionLimit);
    }

    private static bool TryParseLog(ReadOnlyMemory<byte> payload, out LogEntry entry)
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
