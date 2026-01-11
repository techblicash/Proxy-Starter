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
    private readonly ISerializer _serializer;

    public ConfigWriter(ProxyCatalogStore proxyCatalogStore, RulesStore rulesStore)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _rulesStore = rulesStore;
        _serializer = new SerializerBuilder().Build();
    }

    public string EnsureConfig(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);

        var configPath = ResolvePath(settings.ConfigPath, AppPaths.DataDirectory);
        var content = BuildConfig(settings);

        File.WriteAllText(configPath, content, new UTF8Encoding(false));
        return configPath;
    }

    private string BuildConfig(AppSettings settings)
    {
        var proxies = _proxyCatalogStore.LoadProxyDefinitions();
        var proxyNames = new List<string>();
        foreach (var proxy in proxies)
        {
            if (proxy.TryGetValue("name", out var nameValue) && nameValue is not null)
            {
                proxyNames.Add(nameValue.ToString() ?? string.Empty);
            }
        }

        var selectionGroup = settings.SelectionGroup;
        var groups = BuildProxyGroups(selectionGroup, proxyNames);

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
                ["auto-detect-interface"] = true
            },
            ["dns"] = new Dictionary<string, object>
            {
                ["enable"] = true,
                ["listen"] = "0.0.0.0:1053",
                ["enhanced-mode"] = "fake-ip",
                ["nameserver"] = new[] { "223.5.5.5", "1.1.1.1" }
            },
            ["proxies"] = proxies,
            ["proxy-groups"] = groups,
            ["rules"] = BuildRules(settings)
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            config["secret"] = settings.ApiSecret;
        }

        return _serializer.Serialize(config);
    }

    private List<Dictionary<string, object>> BuildProxyGroups(string selectionGroup, List<string> proxyNames)
    {
        var groups = new List<Dictionary<string, object>>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var group in _proxyCatalogStore.LoadProxyGroups())
        {
            var sanitized = SanitizeGroup(group, proxyNames);
            if (sanitized is null)
            {
                continue;
            }

            var name = sanitized.TryGetValue("name", out var nameValue) ? nameValue?.ToString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            groups.Add(sanitized);
        }

        if (!string.IsNullOrWhiteSpace(selectionGroup) && !seen.Contains(selectionGroup))
        {
            groups.Insert(0, new Dictionary<string, object>
            {
                ["name"] = selectionGroup,
                ["type"] = "select",
                ["proxies"] = BuildGroupList(proxyNames)
            });
        }

        return groups;
    }

    private static Dictionary<string, object>? SanitizeGroup(Dictionary<string, object> group, List<string> proxyNames)
    {
        if (!group.TryGetValue("name", out var nameValue))
        {
            return null;
        }

        var name = nameValue?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var result = new Dictionary<string, object>(group, System.StringComparer.OrdinalIgnoreCase);

        if (!result.TryGetValue("type", out var typeValue) || string.IsNullOrWhiteSpace(typeValue?.ToString()))
        {
            result["type"] = "select";
        }

        result.Remove("use");

        if (!result.TryGetValue("proxies", out var proxiesValue) || proxiesValue is null)
        {
            result["proxies"] = BuildGroupList(proxyNames);
            return result;
        }

        var list = new List<string>();
        switch (proxiesValue)
        {
            case IEnumerable<string> strings:
                foreach (var text in strings)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
                break;
            case IEnumerable<object> objects:
                foreach (var item in objects)
                {
                    var text = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
                break;
            default:
                var single = proxiesValue.ToString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    list.Add(single);
                }
                break;
        }

        if (list.Count == 0)
        {
            result["proxies"] = BuildGroupList(proxyNames);
        }
        else
        {
            result["proxies"] = list;
        }

        return result;
    }

    private static List<string> BuildGroupList(List<string> proxies)
    {
        var list = new List<string>
        {
            "DIRECT",
            "REJECT"
        };

        foreach (var proxy in proxies.Distinct())
        {
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                list.Add(proxy);
            }
        }

        return list;
    }

    private List<string> BuildRules(AppSettings settings)
    {
        var selectionGroup = settings.SelectionGroup;
        var rules = _rulesStore.LoadRules().ToList();
        var blockedRules = BuildBlockedRules(settings.BlockedSites);
        if (blockedRules.Count > 0)
        {
            rules.InsertRange(0, blockedRules);
        }

        if (rules.Count == 0)
        {
            return new List<string> { $"MATCH,{selectionGroup}" };
        }

        var hasMatch = rules.Any(rule => rule.StartsWith("MATCH,", System.StringComparison.OrdinalIgnoreCase));
        if (!hasMatch)
        {
            rules.Add($"MATCH,{selectionGroup}");
        }

        return rules;
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
