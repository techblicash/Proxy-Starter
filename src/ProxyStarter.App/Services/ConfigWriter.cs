using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ProxyStarter.App.Models;
using YamlDotNet.Serialization;

namespace ProxyStarter.App.Services;

public sealed class ConfigWriter
{
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly RulesStore _rulesStore;
    private readonly DefaultRulesService _defaultRulesService;
    private readonly ISerializer _serializer;
    private readonly SubscriptionService _subscriptionService;

    public ConfigWriter(
        ProxyCatalogStore proxyCatalogStore,
        RulesStore rulesStore,
        DefaultRulesService defaultRulesService,
        SubscriptionService subscriptionService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _rulesStore = rulesStore;
        _defaultRulesService = defaultRulesService;
        _subscriptionService = subscriptionService;
        _serializer = new SerializerBuilder().Build();
    }

    public (string Path, string? NodeName) EnsureConfig(AppSettings settings, bool requireSelectedNode = false)
    {
        var configPath = ResolvePath(settings.ConfigPath, AppPaths.DataDirectory);
        GeoIpDatabaseHelper.EnsureDatabase(configPath);
        var (nodes, proxies) = _proxyCatalogStore.LoadOutbounds();
        var names = proxies.Select(RoutingDefaults.OutboundName).ToHashSet(StringComparer.Ordinal);
        var activeNode = RoutingDefaults.ResolveSelectedNode(settings, nodes, names, requireSelectedNode);
        var content = BuildConfig(settings, configPath, proxies, activeNode ?? "DIRECT");

        AtomicFile.WriteAllText(configPath, content, new UTF8Encoding(false));
        return (configPath, activeNode);
    }

    private string BuildConfig(
        AppSettings settings, string configPath, IReadOnlyList<Dictionary<string, object>> proxies, string selectedNode)
    {
        var (subscriptionRules, ruleProviders) = settings.Mode == "rule"
            ? _subscriptionService.LoadRoutingRules(settings.SubscriptionRulesProfileId)
            : (Array.Empty<string>(), new Dictionary<string, object>());
        var config = new Dictionary<string, object>
        {
            ["mixed-port"] = settings.MixedPort,
            ["port"] = settings.HttpPort,
            ["socks-port"] = settings.SocksPort,
            ["allow-lan"] = settings.AllowLan,
            // Global routing is expressed with a single rule targeting the selected node.
            ["mode"] = "rule",
            ["log-level"] = settings.LogLevel,
            ["ipv6"] = false,
            ["external-controller"] = $"127.0.0.1:{settings.ApiPort}",
            ["unified-delay"] = true,
            ["tcp-concurrent"] = true,
            ["profile"] = new Dictionary<string, object>
            {
                ["store-fake-ip"] = true
            },
            ["tun"] = new Dictionary<string, object>
            {
                ["enable"] = settings.TunEnabled,
                ["stack"] = "system",
                ["auto-route"] = true,
                ["auto-detect-interface"] = true,
                ["strict-route"] = true,
                ["dns-hijack"] = new[] { "any:53" }
            },
            ["dns"] = MihomoDnsConfig.Build(geoIpFiltering: GeoIpDatabaseHelper.HasDatabase(configPath)),
            ["proxies"] = proxies,
            ["rules"] = BuildRules(settings, selectedNode, subscriptionRules, ruleProviders)
        };

        if (ruleProviders.Count > 0)
        {
            foreach (var provider in ruleProviders.Values.OfType<Dictionary<string, object>>())
            {
                if (provider.TryGetValue("proxy", out var policy) && policy is string target)
                {
                    provider["proxy"] = RoutingDefaults.MapMihomoTarget(target, selectedNode);
                }

                if (string.Equals(provider.GetValueOrDefault("type")?.ToString(), "http", StringComparison.OrdinalIgnoreCase))
                {
                    // Let the core cache HTTP providers by URL when switching between subscriptions.
                    provider.Remove("path");
                }
            }

            config["rule-providers"] = ruleProviders;
        }

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            config["secret"] = settings.ApiSecret;
        }

        return _serializer.Serialize(config);
    }

    private IReadOnlyList<string> BuildRules(
        AppSettings settings, string selectedNode, IReadOnlyList<string> subscriptionRules,
        IReadOnlyDictionary<string, object> ruleProviders)
    {
        if (settings.Mode == "direct")
        {
            return new[] { "MATCH,DIRECT" };
        }

        if (settings.Mode == "global")
        {
            return new[] { $"MATCH,{selectedNode}" };
        }

        return BuildBlockedRules(settings.BlockedSites)
            .Concat(RoutingDefaults.ComposeRules(_rulesStore.LoadRules(), _defaultRulesService.Rules, subscriptionRules, selectedNode))
            .Where(rule => HasRuleProvider(rule, ruleProviders))
            .Select(rule => RoutingDefaults.RewriteRule(rule, target => RoutingDefaults.MapMihomoTarget(target, selectedNode)))
            .ToList();
    }

    private static bool HasRuleProvider(string rule, IReadOnlyDictionary<string, object> ruleProviders)
    {
        var fields = rule.Split(',', 3);
        return !fields[0].Trim().Equals("RULE-SET", StringComparison.OrdinalIgnoreCase)
            || (fields.Length == 3 && ruleProviders.ContainsKey(fields[1].Trim()));
    }

    private static List<string> BuildBlockedRules(string? blockedSites)
    {
        if (string.IsNullOrWhiteSpace(blockedSites))
        {
            return new List<string>();
        }

        var rules = new List<string>();
        var lines = blockedSites
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));

        foreach (var line in lines)
        {
            if (line.Contains(','))
            {
                rules.Add(line);
                continue;
            }

            var host = ExtractHost(line);
            if (TryFormatIpRule(host, out var ipRule))
            {
                rules.Add(ipRule);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(host))
            {
                rules.Add($"DOMAIN-SUFFIX,{host},REJECT");
            }
        }

        return rules;
    }

    private static string ExtractHost(string input)
    {
        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        if (Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var guess) && !string.IsNullOrWhiteSpace(guess.Host))
        {
            return guess.Host;
        }

        return trimmed.TrimStart('*', '.');
    }

    private static bool TryFormatIpRule(string value, out string rule)
    {
        rule = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('/'))
        {
            var parts = trimmed.Split('/', 2);
            if (IPAddress.TryParse(parts[0], out _) && int.TryParse(parts[1], out var prefix))
            {
                rule = $"IP-CIDR,{parts[0]}/{prefix},REJECT";
                return true;
            }
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            rule = $"IP-CIDR,{trimmed}/32,REJECT";
            return true;
        }

        return false;
    }

    private static string ResolvePath(string path, string fallbackDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(fallbackDirectory, path);
    }

}
