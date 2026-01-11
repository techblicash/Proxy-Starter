using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;
using ProxyStarter.App.Models;
using YamlDotNet.RepresentationModel;

namespace ProxyStarter.App.Services;

public sealed class SubscriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SubscriptionStore _subscriptionStore;
    private readonly SubscriptionParser _subscriptionParser;
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly SubscriptionCacheStore _cacheStore;
    private static readonly MediaTypeWithQualityHeaderValue AnyMediaType = new("*/*");

    public SubscriptionService(
        IHttpClientFactory httpClientFactory,
        SubscriptionStore subscriptionStore,
        SubscriptionParser subscriptionParser,
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionCacheStore cacheStore)
    {
        _httpClientFactory = httpClientFactory;
        _subscriptionStore = subscriptionStore;
        _subscriptionParser = subscriptionParser;
        _proxyCatalogStore = proxyCatalogStore;
        _cacheStore = cacheStore;
    }

    public async Task<IReadOnlyList<SubscriptionProfile>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = _subscriptionStore.Load().ToList();
        foreach (var profile in profiles.Where(profile => profile.IsEnabled))
        {
            try
            {
                await RefreshProfileAsync(profile, cancellationToken);
            }
            catch
            {
            }
        }

        RebuildCatalogFromCache(profiles);
        _subscriptionStore.Save(profiles);
        return profiles;
    }

    public async Task<SubscriptionParseResult> RefreshProfileAsync(SubscriptionProfile profile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        var url = ResolveSubscriptionUrl(profile.Url);
        var (client, content) = await DownloadSubscriptionAsync(url, cancellationToken);
        _cacheStore.SaveRaw(profile.Id, content.TrimStart('\uFEFF'));
        var result = _subscriptionParser.Parse(content, profile.Name, profile.Id);

        if (result.Nodes.Count == 0 && TryGetProxyProviderUrls(content, out var providers))
        {
            var combined = await DownloadFromProvidersAsync(client, providers, profile.Name, profile.Id, cancellationToken);
            if (combined.Nodes.Count > 0)
            {
                result = new SubscriptionParseResult
                {
                    Proxies = combined.Proxies,
                    Nodes = combined.Nodes,
                    ProxyGroups = result.ProxyGroups.Count > 0 ? result.ProxyGroups : combined.ProxyGroups
                };
            }
        }

        profile.NodeCount = result.Nodes.Count;
        profile.LastUpdated = DateTimeOffset.Now;
        _cacheStore.Save(profile.Id, result);

        return result;
    }

    public async Task<SubscriptionParseResult> ApplyProfileContentAsync(
        SubscriptionProfile profile,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        var normalized = (content ?? string.Empty).TrimStart('\uFEFF');
        var result = _subscriptionParser.Parse(normalized, profile.Name, profile.Id);

        if (result.Nodes.Count == 0 && TryGetProxyProviderUrls(normalized, out var providers))
        {
            var combined = await DownloadFromProvidersWithFallbackAsync(providers, profile.Name, profile.Id, cancellationToken);
            if (combined.Nodes.Count > 0)
            {
                result = new SubscriptionParseResult
                {
                    Proxies = combined.Proxies,
                    Nodes = combined.Nodes,
                    ProxyGroups = result.ProxyGroups.Count > 0 ? result.ProxyGroups : combined.ProxyGroups
                };
            }
        }

        profile.NodeCount = result.Nodes.Count;
        profile.LastUpdated = DateTimeOffset.Now;
        _cacheStore.Save(profile.Id, result);
        _cacheStore.SaveRaw(profile.Id, normalized);

        return result;
    }

    private async Task<(HttpClient Client, string Content)> DownloadSubscriptionAsync(string url, CancellationToken cancellationToken)
    {
        var directClient = _httpClientFactory.CreateClient("SubscriptionDirect");
        ConfigureSubscriptionClient(directClient);

        try
        {
            var content = await DownloadTextAsync(directClient, url, cancellationToken);
            return (directClient, content);
        }
        catch (Exception directException) when (!cancellationToken.IsCancellationRequested)
        {
            var proxyClient = _httpClientFactory.CreateClient("SubscriptionSystemProxy");
            ConfigureSubscriptionClient(proxyClient);

            try
            {
                var content = await DownloadTextAsync(proxyClient, url, cancellationToken);
                return (proxyClient, content);
            }
            catch (Exception proxyException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Subscription download failed.\n\n" +
                    "Direct:\n" + FormatException(directException) + "\n\n" +
                    "System Proxy:\n" + FormatException(proxyException),
                    proxyException);
            }
        }
    }

    public void RebuildCatalogFromCache(IEnumerable<SubscriptionProfile> profiles)
    {
        var proxies = new List<Dictionary<string, object>>();
        var nodes = new List<ProxyNode>();
        var groups = new List<Dictionary<string, object>>();

        foreach (var profile in profiles.Where(profile => profile.IsEnabled))
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                continue;
            }

            proxies.AddRange(_cacheStore.LoadProxies(profile.Id));
            nodes.AddRange(_cacheStore.LoadNodes(profile.Id));
            groups.AddRange(_cacheStore.LoadGroups(profile.Id));
        }

        _proxyCatalogStore.Save(nodes, proxies, groups);
    }

    private static void ConfigureSubscriptionClient(HttpClient client)
    {
        try
        {
            client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        }
        catch
        {
        }

        try
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashforWindows/0.20.0");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyStarter/0.1");
        }
        catch
        {
        }

        try
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(AnyMediaType);
        }
        catch
        {
        }

        try
        {
            client.DefaultRequestHeaders.AcceptEncoding.Clear();
            client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("deflate"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("br"));
        }
        catch
        {
        }
    }

    private static string FormatException(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException)
        {
            var statusCode = httpRequestException.StatusCode is null
                ? string.Empty
                : $" (HTTP {(int)httpRequestException.StatusCode.Value} {httpRequestException.StatusCode.Value})";

            var details = httpRequestException.Message + statusCode;
            if (httpRequestException.InnerException is not null)
            {
                details += $"\nInner: {httpRequestException.InnerException.GetType().Name}: {httpRequestException.InnerException.Message}";
            }

            return details;
        }

        var message = $"{exception.GetType().Name}: {exception.Message}";
        if (exception.InnerException is not null)
        {
            message += $"\nInner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
        }

        return message;
    }

    private static string ResolveSubscriptionUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var trimmed = url.Trim();
        if (!trimmed.StartsWith("clash://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        if (!string.Equals(uri.Host, "install-config", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(key, "url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(parts[1]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return trimmed;
    }

    private static async Task<string> DownloadTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        bytes = DecompressIfNeeded(bytes, response.Content.Headers.ContentEncoding);

        var encoding = GetEncodingOrUtf8(response.Content.Headers.ContentType?.CharSet);
        return encoding.GetString(bytes);
    }

    private static byte[] DecompressIfNeeded(byte[] bytes, ICollection<string> encodings)
    {
        if (bytes.Length < 2)
        {
            return bytes;
        }

        var encoding = encodings.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoding))
        {
            if (bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                return TryDecompressGzip(bytes) ?? bytes;
            }

            return bytes;
        }

        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecompressGzip(bytes) ?? bytes;
        }

        if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecompressDeflate(bytes) ?? bytes;
        }

        if (encoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecompressBrotli(bytes) ?? bytes;
        }

        return bytes;
    }

    private static byte[]? TryDecompressGzip(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecompressDeflate(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecompressBrotli(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static Encoding GetEncodingOrUtf8(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static bool TryGetProxyProviderUrls(string content, out List<(string Name, string Url)> providers)
    {
        providers = new List<(string Name, string Url)>();

        if (string.IsNullOrWhiteSpace(content) || !LooksLikeYaml(content))
        {
            return false;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                return false;
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return false;
            }

            if (!TryGetChild(root, "proxy-providers", out var providerNode) || providerNode is not YamlMappingNode providerMapping)
            {
                return false;
            }

            foreach (var entry in providerMapping.Children)
            {
                var providerName = (entry.Key as YamlScalarNode)?.Value ?? "provider";
                if (entry.Value is not YamlMappingNode providerSettings)
                {
                    continue;
                }

                if (!TryGetChild(providerSettings, "url", out var urlNode) || urlNode is not YamlScalarNode urlScalar)
                {
                    continue;
                }

                var url = urlScalar.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                providers.Add((providerName, url));
            }
        }
        catch
        {
            providers.Clear();
            return false;
        }

        return providers.Count > 0;
    }

    private static bool LooksLikeYaml(string content)
    {
        return content.Contains(":", StringComparison.Ordinal)
               && (content.Contains("proxies:", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("proxy-providers:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                scalar.Value is not null &&
                scalar.Value.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                return true;
            }
        }

        node = new YamlScalarNode();
        return false;
    }

    private async Task<SubscriptionParseResult> DownloadFromProvidersAsync(
        HttpClient client,
        IReadOnlyList<(string Name, string Url)> providers,
        string profileName,
        string profileId,
        CancellationToken cancellationToken)
    {
        var combinedProxies = new List<Dictionary<string, object>>();
        var combinedNodes = new List<ProxyNode>();
        var combinedGroups = new List<Dictionary<string, object>>();

        foreach (var provider in providers)
        {
            try
            {
                var providerUrl = ResolveSubscriptionUrl(provider.Url);
                var providerContent = await DownloadTextAsync(client, providerUrl, cancellationToken);
                var providerResult = _subscriptionParser.Parse(providerContent, $"{profileName}-{provider.Name}", profileId);
                combinedProxies.AddRange(providerResult.Proxies);
                combinedNodes.AddRange(providerResult.Nodes);
                combinedGroups.AddRange(providerResult.ProxyGroups);
            }
            catch
            {
            }
        }

        return new SubscriptionParseResult
        {
            Proxies = combinedProxies,
            Nodes = combinedNodes,
            ProxyGroups = combinedGroups
        };
    }

    private async Task<SubscriptionParseResult> DownloadFromProvidersWithFallbackAsync(
        IReadOnlyList<(string Name, string Url)> providers,
        string profileName,
        string profileId,
        CancellationToken cancellationToken)
    {
        var directClient = _httpClientFactory.CreateClient("SubscriptionDirect");
        ConfigureSubscriptionClient(directClient);

        var directResult = await DownloadFromProvidersAsync(directClient, providers, profileName, profileId, cancellationToken);
        if (directResult.Nodes.Count > 0)
        {
            return directResult;
        }

        var proxyClient = _httpClientFactory.CreateClient("SubscriptionSystemProxy");
        ConfigureSubscriptionClient(proxyClient);

        var proxyResult = await DownloadFromProvidersAsync(proxyClient, providers, profileName, profileId, cancellationToken);
        return proxyResult.Nodes.Count > 0 ? proxyResult : directResult;
    }
}
