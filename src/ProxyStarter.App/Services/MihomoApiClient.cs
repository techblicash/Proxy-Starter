using System;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettingsStore _settingsStore;
    private readonly SemaphoreSlim _proxiesCacheGate = new(1, 1);
    private MihomoProxiesSnapshot? _proxiesCache;
    private DateTimeOffset _proxiesCacheAt;
    private static readonly TimeSpan ProxiesCacheTtl = TimeSpan.FromSeconds(2);

    public MihomoApiClient(IHttpClientFactory httpClientFactory, AppSettingsStore settingsStore)
    {
        _httpClientFactory = httpClientFactory;
        _settingsStore = settingsStore;
    }

    public async Task<MihomoProxiesSnapshot?> GetProxiesAsync(CancellationToken cancellationToken = default)
    {
        var cached = _proxiesCache;
        if (cached is not null && DateTimeOffset.UtcNow - _proxiesCacheAt < ProxiesCacheTtl)
        {
            return cached;
        }

        await _proxiesCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _proxiesCache;
            if (cached is not null && DateTimeOffset.UtcNow - _proxiesCacheAt < ProxiesCacheTtl)
            {
                return cached;
            }

            using var client = CreateClient();
            using var response = await client.GetAsync("proxies", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("proxies", out var proxiesElement))
            {
                return null;
            }

            var entries = new Dictionary<string, MihomoProxy>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in proxiesElement.EnumerateObject())
            {
                var proxy = ParseProxy(property.Name, property.Value);
                entries[property.Name] = proxy;
            }

            var snapshot = new MihomoProxiesSnapshot(entries);
            _proxiesCache = snapshot;
            _proxiesCacheAt = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            _proxiesCacheGate.Release();
        }
    }

    public async Task<string?> GetSelectedProxyAsync(string groupName, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetProxiesAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        return snapshot.Proxies.TryGetValue(groupName, out var proxy) ? proxy.Now : null;
    }

    public async Task<bool> SetProxySelectionAsync(string groupName, string proxyName, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var payload = JsonSerializer.Serialize(new { name = proxyName });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"proxies/{Uri.EscapeDataString(groupName)}", content, cancellationToken).ConfigureAwait(false);
        var success = response.IsSuccessStatusCode;
        if (success)
        {
            InvalidateProxiesCache();
        }

        return success;
    }

    public async Task<int> TestDelayAsync(string proxyName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var url = $"proxies/{Uri.EscapeDataString(proxyName)}/delay?timeout={timeoutMs}&url=https://www.gstatic.com/generate_204";
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return -1;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("delay", out var delayElement) && delayElement.TryGetInt32(out var delay))
        {
            return delay;
        }

        return -1;
    }

    public async Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var payload = JsonSerializer.Serialize(new { mode });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), "configs")
        {
            Content = content
        };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var success = response.IsSuccessStatusCode;
        if (success)
        {
            InvalidateProxiesCache();
        }

        return success;
    }

    public async Task<MihomoConnectionSnapshot?> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("connections", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var uploadTotal = root.TryGetProperty("uploadTotal", out var uploadElement) ? uploadElement.GetInt64() : 0L;
        var downloadTotal = root.TryGetProperty("downloadTotal", out var downloadElement) ? downloadElement.GetInt64() : 0L;

        string? address = null;
        var connections = new List<MihomoConnection>();
        if (root.TryGetProperty("connections", out var connectionsElement) && connectionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var connection in connectionsElement.EnumerateArray())
            {
                if (!connection.TryGetProperty("metadata", out var metadata))
                {
                    continue;
                }

                address = ExtractAddress(metadata);
                if (!string.IsNullOrWhiteSpace(address))
                {
                    break;
                }
            }

            foreach (var connection in connectionsElement.EnumerateArray())
            {
                var parsed = ParseConnection(connection);
                if (parsed is not null)
                {
                    connections.Add(parsed);
                }
            }
        }

        return new MihomoConnectionSnapshot(uploadTotal, downloadTotal, address, connections);
    }

    public async Task<bool> CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.DeleteAsync("connections", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CloseConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        using var client = CreateClient();
        using var response = await client.DeleteAsync($"connections/{Uri.EscapeDataString(id)}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri($"http://127.0.0.1:{_settingsStore.Settings.ApiPort}/");
        var secret = _settingsStore.Settings.ApiSecret;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        return client;
    }

    private static MihomoProxy ParseProxy(string name, JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        var now = element.TryGetProperty("now", out var nowElement) ? nowElement.GetString() : null;
        var all = new List<string>();

        if (element.TryGetProperty("all", out var allElement) && allElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in allElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    all.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        return new MihomoProxy(name, type, now, all);
    }

    private static string? ExtractAddress(JsonElement metadata)
    {
        if (metadata.TryGetProperty("host", out var hostElement))
        {
            var host = hostElement.GetString();
            if (!string.IsNullOrWhiteSpace(host))
            {
                return host;
            }
        }

        if (metadata.TryGetProperty("destinationIP", out var ipElement))
        {
            var ip = ipElement.GetString();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                return ip;
            }
        }

        return null;
    }

    private static MihomoConnection? ParseConnection(JsonElement connection)
    {
        if (!connection.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        var id = idElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var upload = connection.TryGetProperty("upload", out var uploadElement) ? uploadElement.GetInt64() : 0L;
        var download = connection.TryGetProperty("download", out var downloadElement) ? downloadElement.GetInt64() : 0L;
        var rule = connection.TryGetProperty("rule", out var ruleElement) ? ruleElement.GetString() ?? string.Empty : string.Empty;
        var proxy = connection.TryGetProperty("proxy", out var proxyElement) ? proxyElement.GetString() ?? string.Empty : string.Empty;

        var start = DateTimeOffset.Now;
        if (connection.TryGetProperty("start", out var startElement))
        {
            var startText = startElement.GetString();
            if (!string.IsNullOrWhiteSpace(startText) && DateTimeOffset.TryParse(startText, out var parsed))
            {
                start = parsed;
            }
        }

        var chains = new List<string>();
        if (connection.TryGetProperty("chains", out var chainsElement) && chainsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in chainsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var chain = item.GetString();
                    if (!string.IsNullOrWhiteSpace(chain))
                    {
                        chains.Add(chain);
                    }
                }
            }
        }

        if (!connection.TryGetProperty("metadata", out var metadata))
        {
            return new MihomoConnection(id, string.Empty, string.Empty, string.Empty, rule, proxy, string.Empty, upload, download, start, chains);
        }

        var network = metadata.TryGetProperty("network", out var networkElement) ? networkElement.GetString() ?? string.Empty : string.Empty;
        var type = metadata.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;

        var host = metadata.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null;
        var destinationIp = metadata.TryGetProperty("destinationIP", out var destinationElement) ? destinationElement.GetString() : null;
        var destinationPort = TryGetInt(metadata, "destinationPort");
        var targetHost = !string.IsNullOrWhiteSpace(host) ? host : destinationIp ?? string.Empty;
        var target = targetHost;
        if (!string.IsNullOrWhiteSpace(targetHost) && destinationPort > 0 && !targetHost.Contains(":", StringComparison.Ordinal))
        {
            target = $"{targetHost}:{destinationPort}";
        }

        var processPath = metadata.TryGetProperty("processPath", out var processElement) ? processElement.GetString() : null;
        var processName = string.IsNullOrWhiteSpace(processPath) ? string.Empty : Path.GetFileName(processPath);

        return new MihomoConnection(id, target, network, type, rule, proxy, processName, upload, download, start, chains);
    }

    private static int TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var textNumber))
        {
            return textNumber;
        }

        return 0;
    }

    private void InvalidateProxiesCache()
    {
        _proxiesCache = null;
        _proxiesCacheAt = default;
    }
}
