using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
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
    private readonly object _sync = new();

    public SubscriptionCacheStore(
        AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
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

        lock (_sync)
        {
            Try(() => Directory.CreateDirectory(_cacheDirectory), "Create cache directory");

            Try(() =>
            {
                var nodesJson = JsonSerializer.Serialize(result.Nodes, Options);
                AtomicFile.WriteAllText(GetNodesPath(profileId), nodesJson, new UTF8Encoding(false));
            }, "Save nodes cache");

            Try(() =>
            {
                var proxiesYaml = _serializer.Serialize(result.Proxies);
                AtomicFile.WriteAllText(GetProxiesPath(profileId), proxiesYaml, new UTF8Encoding(false));
            }, "Save proxies cache");

            Try(() =>
            {
                var ruleProvidersYaml = _serializer.Serialize(result.RuleProviders);
                AtomicFile.WriteAllText(GetRuleProvidersPath(profileId), ruleProvidersYaml, new UTF8Encoding(false));
            }, "Save rule providers cache");

            Try(() =>
            {
                var rulesText = string.Join(Environment.NewLine, result.Rules ?? Array.Empty<string>());
                AtomicFile.WriteAllText(GetRulesPath(profileId), rulesText, new UTF8Encoding(false));
            }, "Save rules cache");
        }
    }

    public void SaveRaw(string profileId, string content)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        lock (_sync)
        {
            Try(() => Directory.CreateDirectory(_cacheDirectory), "Create cache directory");

            Try(() =>
            {
                AtomicFile.WriteAllText(GetRawPath(profileId), content ?? string.Empty, new UTF8Encoding(false));
            }, "Save raw cache");
        }
    }

    public string LoadRaw(string profileId)
    {
        try
        {
            lock (_sync)
            {
                return AtomicFile.ReadAllTextOrEmpty(GetRawPath(profileId));
            }
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

            var ruleProviders = LoadRuleProviders(profileId);
            if (ruleProviders.Count > 0 && !root.ContainsKey("rule-providers"))
            {
                root["rule-providers"] = ruleProviders;
            }

            var rules = LoadRules(profileId);
            if (rules.Count > 0 || !root.ContainsKey("rules"))
            {
                root["rules"] = rules;
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
            lock (_sync)
            {
                foreach (var json in AtomicFile.ReadTextCandidates(GetNodesPath(profileId)))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<List<ProxyNode>>(json) ?? new List<ProxyNode>();
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log(ex, "SubscriptionCacheStore: Load nodes");
                    }
                }
            }

            return new List<ProxyNode>();
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
            lock (_sync)
            {
                foreach (var yaml in AtomicFile.ReadTextCandidates(GetProxiesPath(profileId)))
                {
                    try
                    {
                        return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                               ?? new List<Dictionary<string, object>>();
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log(ex, "SubscriptionCacheStore: Load proxies");
                    }
                }

                return new List<Dictionary<string, object>>();
            }
        }
        catch
        {
            return new List<Dictionary<string, object>>();
        }
    }

    public IReadOnlyDictionary<string, object> LoadRuleProviders(string profileId)
    {
        try
        {
            lock (_sync)
            {
                foreach (var yaml in AtomicFile.ReadTextCandidates(GetRuleProvidersPath(profileId)))
                {
                    try
                    {
                        using var reader = new StringReader(yaml);
                        var deserialized = _deserializer.Deserialize<object>(reader);
                        return NormalizeMapping(deserialized) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log(ex, "SubscriptionCacheStore: Load rule providers");
                    }
                }
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> LoadRules(string profileId)
    {
        try
        {
            lock (_sync)
            {
                foreach (var text in AtomicFile.ReadTextCandidates(GetRulesPath(profileId)))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            return Array.Empty<string>();
                        }

                        return text
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim())
                            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log(ex, "SubscriptionCacheStore: Load rules");
                    }
                }
            }

            return new List<string>();
        }
        catch
        {
            return new List<string>();
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

    private string GetRuleProvidersPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.rule-providers.yaml");
    }

    private string GetRulesPath(string profileId)
    {
        return Path.Combine(_cacheDirectory, $"{profileId}.rules.txt");
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
            ["auto-detect-interface"] = true,
            ["strict-route"] = true,
            ["dns-hijack"] = new[] { "any:53" }
        };
        root["dns"] = MihomoDnsConfig.Build();

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            root["secret"] = settings.ApiSecret;
        }

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
                ["auto-detect-interface"] = true,
                ["strict-route"] = true,
                ["dns-hijack"] = new[] { "any:53" }
            };
        }

        if (!root.ContainsKey("dns"))
        {
            root["dns"] = MihomoDnsConfig.Build();
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
