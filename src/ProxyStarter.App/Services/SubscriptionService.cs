using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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
        var result = _subscriptionParser.Parse(content, profile.Name, profile.Id);
        if (TryGetProxyProviderUrls(content, out var providers))
        {
            var downloaded = await DownloadFromProvidersAsync(client, providers, profile.Name, profile.Id, cancellationToken);
            result = MergeProviders(result, downloaded);
        }

        _cacheStore.SaveRaw(profile.Id, content.TrimStart('\uFEFF'));
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

        if (TryGetProxyProviderUrls(normalized, out var providers))
        {
            var downloaded = await DownloadFromProvidersWithFallbackAsync(providers, profile.Name, profile.Id, cancellationToken);
            result = MergeProviders(result, downloaded);
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
        var ruleProviders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<string>();

        var outboundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL", "block"
        };
        var providerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles.Where(profile => profile.IsEnabled && !string.IsNullOrWhiteSpace(profile.Id)))
        {
            var parsed = LoadProfileConfig(profile);
            var profileNodes = parsed.Proxies.Select(RoutingDefaults.GroupName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in profileNodes.Concat(parsed.ProxyGroups.Select(RoutingDefaults.GroupName))
                         .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                names[name] = UniqueName(outboundNames, name, profile.Name);
            }

            string MapName(string name) => names.GetValueOrDefault(name, name);

            foreach (var proxy in parsed.Proxies.DistinctBy(RoutingDefaults.GroupName, StringComparer.OrdinalIgnoreCase))
            {
                var name = RoutingDefaults.GroupName(proxy);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                proxy["name"] = MapName(name);
                RewriteReference(proxy, "dialer-proxy", MapName);
                proxies.Add(proxy);
            }

            foreach (var node in parsed.Nodes.DistinctBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
            {
                node.Name = MapName(node.Name);
                node.SourceId = profile.Id;
                nodes.Add(node);
            }

            foreach (var group in parsed.ProxyGroups.DistinctBy(RoutingDefaults.GroupName, StringComparer.OrdinalIgnoreCase))
            {
                var name = RoutingDefaults.GroupName(group);
                if (string.IsNullOrWhiteSpace(name) || profileNodes.Contains(name))
                {
                    continue;
                }

                PrepareGroup(group, profileNodes);
                group["name"] = MapName(name);
                group["proxies"] = RoutingDefaults.Names(group, "proxies").Select(MapName).ToList();
                RewriteReference(group, "default-selected", MapName);
                RewriteReference(group, "empty-fallback", MapName);
                group[RoutingDefaults.GroupSourceKey] = profile.Id;
                groups.Add(group);
            }

            var providerAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in parsed.RuleProviders)
            {
                var name = UniqueName(providerNames, provider.Key, profile.Name);
                providerAliases[provider.Key] = name;
                if (name != provider.Key && provider.Value is Dictionary<string, object> definition
                    && definition.ContainsKey("url"))
                {
                    definition.Remove("path");
                }
                ruleProviders[name] = provider.Value;
            }

            rules.AddRange(parsed.Rules.Select(rule => RoutingDefaults.RewriteRule(
                rule, MapName, name => providerAliases.GetValueOrDefault(name, name))));
        }

        _proxyCatalogStore.Save(nodes, proxies, groups, ruleProviders, rules);
    }

    private SubscriptionParseResult LoadProfileConfig(SubscriptionProfile profile)
    {
        var raw = _cacheStore.LoadRaw(profile.Id);
        var parsed = _subscriptionParser.Parse(raw, profile.Name, profile.Id);
        if (parsed.Nodes.Count > 0 && !TryGetProxyProviderUrls(raw, out _))
        {
            _cacheStore.Save(profile.Id, parsed);
            return parsed;
        }

        var groups = _cacheStore.LoadGroups(profile.Id);
        return new SubscriptionParseResult
        {
            Proxies = _cacheStore.LoadProxies(profile.Id),
            Nodes = _cacheStore.LoadNodes(profile.Id),
            ProxyGroups = groups.Count > 0 ? groups : parsed.ProxyGroups,
            RuleProviders = parsed.RuleProviders.Count > 0 ? parsed.RuleProviders : _cacheStore.LoadRuleProviders(profile.Id),
            Rules = parsed.Rules.Count > 0 ? parsed.Rules : _cacheStore.LoadRules(profile.Id)
        };
    }

    private static void PrepareGroup(Dictionary<string, object> group, IEnumerable<string> profileNodes)
    {
        if (string.IsNullOrWhiteSpace(group.GetValueOrDefault("type")?.ToString()))
        {
            group["type"] = "select";
        }

        var members = RoutingDefaults.Names(group, "proxies").ToList();
        var fromProviders = group.Remove("use");
        var includeAll = group.Remove("include-all", out var all) && IsTrue(all);
        var includeProxies = group.Remove("include-all-proxies", out var proxies) && IsTrue(proxies);
        var includeProviders = group.Remove("include-all-providers", out var providers) && IsTrue(providers);
        if (fromProviders || includeAll || includeProxies || includeProviders)
        {
            members.AddRange(FilterNodes(group, profileNodes.OrderBy(name => name, StringComparer.Ordinal)));
        }

        group["proxies"] = members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FilterNodes(Dictionary<string, object> group, IEnumerable<string> nodes)
    {
        var include = group.GetValueOrDefault("filter")?.ToString();
        var exclude = group.GetValueOrDefault("exclude-filter")?.ToString();
        try
        {
            var timeout = TimeSpan.FromMilliseconds(100);
            var includeFilter = string.IsNullOrWhiteSpace(include) ? null : new Regex(include, RegexOptions.None, timeout);
            var excludeFilter = string.IsNullOrWhiteSpace(exclude) ? null : new Regex(exclude, RegexOptions.None, timeout);
            return nodes.Where(name => (includeFilter?.IsMatch(name) ?? true)
                && !(excludeFilter?.IsMatch(name) ?? false)).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            CrashLogger.Log(ex, "SubscriptionService: Group filter");
            return Array.Empty<string>();
        }
    }

    private static bool IsTrue(object? value) =>
        bool.TryParse(value?.ToString(), out var enabled) && enabled;

    private static void RewriteReference(Dictionary<string, object> outbound, string key, Func<string, string> mapName)
    {
        if (outbound.TryGetValue(key, out var value) && value is string name)
        {
            outbound[key] = mapName(name);
        }
    }

    private static string UniqueName(HashSet<string> used, string name, string profileName)
    {
        if (used.Add(name))
        {
            return name;
        }

        var prefix = $"{name} [{profileName}]";
        var candidate = prefix;
        for (var index = 2; !used.Add(candidate); index++)
        {
            candidate = $"{prefix} {index}";
        }

        return candidate;
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

        if (!content.Contains("proxy-providers", StringComparison.OrdinalIgnoreCase))
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

    private static SubscriptionParseResult MergeProviders(
        SubscriptionParseResult subscription, IReadOnlyDictionary<string, SubscriptionParseResult> providers)
    {
        var providerProxies = providers.Values.SelectMany(result => result.Proxies).ToList();
        var proxies = subscription.Proxies.Concat(providerProxies).DistinctBy(RoutingDefaults.GroupName).ToList();
        var nodes = subscription.Nodes.Concat(providers.Values.SelectMany(result => result.Nodes))
            .DistinctBy(node => node.Name).ToList();
        var groups = subscription.ProxyGroups.Count > 0
            ? subscription.ProxyGroups
            : providers.Values.SelectMany(result => result.ProxyGroups).ToList();

        foreach (var group in groups)
        {
            var providerNames = RoutingDefaults.Names(group, "use").ToList();
            var includesAll = IsTrue(group.GetValueOrDefault("include-all"))
                || IsTrue(group.GetValueOrDefault("include-all-providers"));
            var members = includesAll ? providerProxies : providerNames
                .Where(providers.ContainsKey)
                .SelectMany(name => providers[name].Proxies);
            var expanded = FilterNodes(group, members.Select(RoutingDefaults.GroupName));
            group["proxies"] = RoutingDefaults.Names(group, "proxies").Concat(expanded).ToList();
            group.Remove("use");
            group.Remove("include-all-providers");
        }

        return new SubscriptionParseResult
        {
            Proxies = proxies,
            Nodes = nodes,
            ProxyGroups = groups,
            RuleProviders = subscription.RuleProviders,
            Rules = subscription.Rules
        };
    }

    private async Task<Dictionary<string, SubscriptionParseResult>> DownloadFromProvidersAsync(
        HttpClient client,
        IReadOnlyList<(string Name, string Url)> providers,
        string profileName,
        string profileId,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, SubscriptionParseResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            try
            {
                var providerUrl = ResolveSubscriptionUrl(provider.Url);
                var providerContent = await DownloadTextAsync(client, providerUrl, cancellationToken);
                results[provider.Name] = _subscriptionParser.Parse(providerContent, $"{profileName}-{provider.Name}", profileId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                CrashLogger.Log(ex, "SubscriptionService: Download proxy provider");
            }
        }

        return results;
    }

    private async Task<Dictionary<string, SubscriptionParseResult>> DownloadFromProvidersWithFallbackAsync(
        IReadOnlyList<(string Name, string Url)> providers,
        string profileName,
        string profileId,
        CancellationToken cancellationToken)
    {
        var directClient = _httpClientFactory.CreateClient("SubscriptionDirect");
        ConfigureSubscriptionClient(directClient);

        var results = await DownloadFromProvidersAsync(directClient, providers, profileName, profileId, cancellationToken);
        var missing = providers.Where(provider => !results.TryGetValue(provider.Name, out var result)
            || result.Nodes.Count == 0).ToList();
        if (missing.Count > 0)
        {
            var proxyClient = _httpClientFactory.CreateClient("SubscriptionSystemProxy");
            ConfigureSubscriptionClient(proxyClient);

            var fallback = await DownloadFromProvidersAsync(proxyClient, missing, profileName, profileId, cancellationToken);
            foreach (var provider in fallback)
            {
                results[provider.Key] = provider.Value;
            }
        }

        return results;
    }
}
