using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyStarter.App.Models;
using YamlDotNet.Serialization;

namespace ProxyStarter.App.Services;

public sealed class SubscriptionCacheStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _cacheDirectory;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private readonly AppSettingsStore _settingsStore;
    private readonly RulesStore _rulesStore;

    public SubscriptionCacheStore(AppSettingsStore settingsStore, RulesStore rulesStore)
    {
        _settingsStore = settingsStore;
        _rulesStore = rulesStore;
        _cacheDirectory = Path.Combine(AppPaths.DataDirectory, "subscriptions");
        _serializer = new SerializerBuilder().Build();
        _deserializer = new DeserializerBuilder().Build();
    }

    public void Save(string profileId, SubscriptionParseResult result)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        Try(() => Directory.CreateDirectory(_cacheDirectory), "Create cache directory");

        Try(() =>
        {
            var nodesJson = JsonSerializer.Serialize(result.Nodes, Options);
            File.WriteAllText(GetNodesPath(profileId), nodesJson, new UTF8Encoding(false));
        }, "Save nodes cache");

        Try(() =>
        {
            var proxiesYaml = _serializer.Serialize(result.Proxies);
            File.WriteAllText(GetProxiesPath(profileId), proxiesYaml, new UTF8Encoding(false));
        }, "Save proxies cache");

        Try(() =>
        {
            var groupsYaml = _serializer.Serialize(result.ProxyGroups);
            File.WriteAllText(GetGroupsPath(profileId), groupsYaml, new UTF8Encoding(false));
        }, "Save groups cache");
    }

    public void SaveRaw(string profileId, string content)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        Try(() => Directory.CreateDirectory(_cacheDirectory), "Create cache directory");

        Try(() =>
        {
            File.WriteAllText(GetRawPath(profileId), content ?? string.Empty, new UTF8Encoding(false));
        }, "Save raw cache");
    }

    public string LoadRaw(string profileId)
    {
        try
        {
            var path = GetRawPath(profileId);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    public string BuildEditableYaml(string profileId, string? baseContent = null)
    {
        try
        {
            var proxies = LoadProxies(profileId);
            var groups = LoadGroups(profileId);

            var root = DeserializeYamlMapping(baseContent);
            if (root.Count == 0)
            {
                ApplyDefaultConfig(root, _settingsStore.Settings);
            }
            else
            {
                EnsureDefaults(root, _settingsStore.Settings);
            }

            if (proxies.Count > 0 || !root.ContainsKey("proxies"))
            {
                root["proxies"] = proxies;
            }

            if (groups.Count > 0 || !root.ContainsKey("proxy-groups"))
            {
                root["proxy-groups"] = groups;
            }

            if (!root.ContainsKey("rules"))
            {
                root["rules"] = BuildRules(_settingsStore.Settings);
            }

            return _serializer.Serialize(root);
        }
        catch
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<ProxyNode> LoadNodes(string profileId)
    {
        try
        {
            var path = GetNodesPath(profileId);
            if (!File.Exists(path))
            {
                return new List<ProxyNode>();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ProxyNode>>(json) ?? new List<ProxyNode>();
        }
        catch
        {
            return new List<ProxyNode>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadProxies(string profileId)
    {
        try
        {
            var path = GetProxiesPath(profileId);
            if (!File.Exists(path))
            {
                return new List<Dictionary<string, object>>();
            }

            var yaml = File.ReadAllText(path);
            return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                   ?? new List<Dictionary<string, object>>();
        }
        catch
        {
            return new List<Dictionary<string, object>>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadGroups(string profileId)
    {
        try
        {
            var path = GetGroupsPath(profileId);
            if (!File.Exists(path))
            {
                return new List<Dictionary<string, object>>();
            }

            var yaml = File.ReadAllText(path);
            return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                   ?? new List<Dictionary<string, object>>();
        }
        catch
        {
            return new List<Dictionary<string, object>>();
        }
    }

    private string GetNodesPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.nodes.json");
    }

    private string GetProxiesPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.proxies.yaml");
    }

    private string GetGroupsPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.groups.yaml");
    }

    private string GetRawPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.raw.txt");
    }

    private Dictionary<string, object> DeserializeYamlMapping(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var reader = new StringReader(content);
            var deserialized = _deserializer.Deserialize<object>(reader);
            return NormalizeMapping(deserialized) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object>? NormalizeMapping(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object> stringMap:
                return NormalizeMapping((IDictionary<string, object>)stringMap);
            case IDictionary<string, object> map:
                return NormalizeMapping(map);
            case IDictionary<object, object> objectMap:
                return NormalizeMapping(objectMap);
            default:
                return null;
        }
    }

    private static Dictionary<string, object> NormalizeMapping(IDictionary<string, object> map)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            result[pair.Key] = NormalizeValue(pair.Value) ?? string.Empty;
        }

        return result;
    }

    private static Dictionary<string, object> NormalizeMapping(IDictionary<object, object> map)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            var key = pair.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = NormalizeValue(pair.Value) ?? string.Empty;
        }

        return result;
    }

    private static object? NormalizeValue(object? value)
    {
        switch (value)
        {
            case IDictionary<object, object> objectMap:
                return NormalizeMapping(objectMap);
            case IDictionary<string, object> stringMap:
                return NormalizeMapping(stringMap);
            case IEnumerable<object> list:
                return list.Select(NormalizeValue).Where(item => item is not null).ToList();
            default:
                return value;
        }
    }

    private void ApplyDefaultConfig(Dictionary<string, object> root, AppSettings settings)
    {
        root["mixed-port"] = settings.MixedPort;
        root["port"] = settings.HttpPort;
        root["socks-port"] = settings.SocksPort;
        root["allow-lan"] = settings.AllowLan;
        root["mode"] = settings.Mode;
        root["log-level"] = settings.LogLevel;
        root["ipv6"] = false;
        root["external-controller"] = $"127.0.0.1:{settings.ApiPort}";
        root["unified-delay"] = true;
        root["tcp-concurrent"] = true;
        root["profile"] = new Dictionary<string, object>
        {
            ["store-selected"] = true,
            ["store-fake-ip"] = true
        };
        root["tun"] = new Dictionary<string, object>
        {
            ["enable"] = settings.TunEnabled,
            ["stack"] = "system",
            ["auto-route"] = true,
            ["auto-detect-interface"] = true
        };
        root["dns"] = new Dictionary<string, object>
        {
            ["enable"] = true,
            ["listen"] = "0.0.0.0:1053",
            ["enhanced-mode"] = "fake-ip",
            ["nameserver"] = new[] { "223.5.5.5", "1.1.1.1" }
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            root["secret"] = settings.ApiSecret;
        }

        root["rules"] = BuildRules(settings);
    }

    private void EnsureDefaults(Dictionary<string, object> root, AppSettings settings)
    {
        SetIfMissing(root, "mixed-port", settings.MixedPort);
        SetIfMissing(root, "port", settings.HttpPort);
        SetIfMissing(root, "socks-port", settings.SocksPort);
        SetIfMissing(root, "allow-lan", settings.AllowLan);
        SetIfMissing(root, "mode", settings.Mode);
        SetIfMissing(root, "log-level", settings.LogLevel);
        SetIfMissing(root, "ipv6", false);
        SetIfMissing(root, "external-controller", $"127.0.0.1:{settings.ApiPort}");
        SetIfMissing(root, "unified-delay", true);
        SetIfMissing(root, "tcp-concurrent", true);

        if (!root.TryGetValue("profile", out var profileValue) || profileValue is not Dictionary<string, object> profile)
        {
            profile = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            root["profile"] = profile;
        }

        SetIfMissing(profile, "store-selected", true);
        SetIfMissing(profile, "store-fake-ip", true);

        if (!root.ContainsKey("tun"))
        {
            root["tun"] = new Dictionary<string, object>
            {
                ["enable"] = settings.TunEnabled,
                ["stack"] = "system",
                ["auto-route"] = true,
                ["auto-detect-interface"] = true
            };
        }

        if (!root.ContainsKey("dns"))
        {
            root["dns"] = new Dictionary<string, object>
            {
                ["enable"] = true,
                ["listen"] = "0.0.0.0:1053",
                ["enhanced-mode"] = "fake-ip",
                ["nameserver"] = new[] { "223.5.5.5", "1.1.1.1" }
            };
        }

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            SetIfMissing(root, "secret", settings.ApiSecret);
        }
    }

    private static void SetIfMissing(Dictionary<string, object> root, string key, object value)
    {
        if (!root.ContainsKey(key))
        {
            root[key] = value;
        }
    }

    private IReadOnlyList<string> BuildRules(AppSettings settings)
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

        var hasMatch = rules.Any(rule => rule.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase));
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

    private static void Try(System.Action action, string context)
    {
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            CrashLogger.Log(ex, $"SubscriptionCacheStore: {context}");
        }
    }
}
