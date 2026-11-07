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

    public ConfigWriter(
        ProxyCatalogStore proxyCatalogStore,
        RulesStore rulesStore,
        DefaultRulesService defaultRulesService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _rulesStore = rulesStore;
        _defaultRulesService = defaultRulesService;
        _serializer = new SerializerBuilder().Build();
    }

    public string EnsureConfig(AppSettings settings)
    {
        var configPath = ResolvePath(settings.ConfigPath, AppPaths.DataDirectory);
        GeoIpDatabaseHelper.EnsureDatabase(configPath);
        var content = BuildConfig(settings, configPath);

        AtomicFile.WriteAllText(configPath, content, new UTF8Encoding(false));
        return configPath;
    }

    private string BuildConfig(AppSettings settings, string configPath)
    {
        var proxies = _proxyCatalogStore.LoadProxyDefinitions();
        var importedGroups = _proxyCatalogStore.LoadProxyGroups();
        var groups = importedGroups.Select(group => new Dictionary<string, object>(group)).ToList();
        foreach (var group in groups)
        {
            group.Remove(RoutingDefaults.GroupSourceKey);
        }

        var nodes = _proxyCatalogStore.LoadNodes();
        var flatNodes = RoutingDefaults.GetFlatNodes(nodes, importedGroups);
        var flatGroup = RoutingDefaults.GetFlatGroupName(nodes, importedGroups);
        if (flatNodes.Count > 0)
        {
            groups.Add(new Dictionary<string, object>
            {
                ["name"] = flatGroup,
                ["type"] = "select",
                ["hidden"] = true,
                ["proxies"] = flatNodes.Select(node => node.Name).Distinct().Concat(new[] { "DIRECT", "REJECT" }).ToList()
            });
        }

        var selectionGroup = RoutingDefaults.GetDefaultGroupName(importedGroups, flatNodes.Count > 0, flatGroup);
        var ruleProviders = _proxyCatalogStore.LoadRuleProviders();

        var config = new Dictionary<string, object>
        {
            ["mixed-port"] = settings.MixedPort,
            ["port"] = settings.HttpPort,
            ["socks-port"] = settings.SocksPort,
            ["allow-lan"] = settings.AllowLan,
            ["mode"] = settings.Mode,
            ["log-level"] = settings.LogLevel,
            ["ipv6"] = false,
            ["external-controller"] = $"127.0.0.1:{settings.ApiPort}",
            ["unified-delay"] = true,
            ["tcp-concurrent"] = true,
            ["profile"] = new Dictionary<string, object>
            {
                ["store-selected"] = true,
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
            ["proxy-groups"] = groups,
            ["rules"] = BuildRules(settings, selectionGroup, proxies, groups)
        };

        if (ruleProviders.Count > 0)
        {
            config["rule-providers"] = ruleProviders;
        }

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            config["secret"] = settings.ApiSecret;
        }

        return _serializer.Serialize(config);
    }

    private List<string> BuildRules(
        AppSettings settings, string fallback,
        IReadOnlyList<Dictionary<string, object>> proxies, IReadOnlyList<Dictionary<string, object>> groups)
    {
        var targets = proxies.Concat(groups).Select(RoutingDefaults.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        targets.UnionWith(new[] { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL" });
        string MapTarget(string target) => targets.TryGetValue(target, out var existing) ? existing : fallback;

        return BuildBlockedRules(settings.BlockedSites)
            .Concat(RoutingDefaults.ComposeRules(
                _rulesStore.LoadRules(), _defaultRulesService.Rules, _proxyCatalogStore.LoadRules(), fallback)
                .Select(rule => RoutingDefaults.RewriteRule(rule, MapTarget)))
            .ToList();
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
