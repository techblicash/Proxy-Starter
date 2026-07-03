using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProxyStarter.App.Models;
using YamlDotNet.Serialization;

namespace ProxyStarter.App.Services;

public sealed class ProxyCatalogStore
{
    private readonly string _nodesPath;
    private readonly string _proxiesPath;
    private readonly string _groupsPath;
    private readonly string _ruleProvidersPath;
    private readonly string _rulesPath;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private readonly object _sync = new();

    public ProxyCatalogStore()
    {
        _nodesPath = Path.Combine(AppPaths.DataDirectory, "nodes.json");
        _proxiesPath = Path.Combine(AppPaths.DataDirectory, "proxies.yaml");
        _groupsPath = Path.Combine(AppPaths.DataDirectory, "groups.yaml");
        _ruleProvidersPath = Path.Combine(AppPaths.DataDirectory, "rule-providers.yaml");
        _rulesPath = Path.Combine(AppPaths.DataDirectory, "subscription-rules.txt");
        _serializer = new SerializerBuilder().Build();
        _deserializer = new DeserializerBuilder().Build();
    }

    public IReadOnlyList<ProxyNode> LoadNodes()
    {
        lock (_sync)
        {
            foreach (var json in AtomicFile.ReadTextCandidates(_nodesPath))
            {
                try
                {
                    return JsonSerializer.Deserialize<List<ProxyNode>>(json) ?? new List<ProxyNode>();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "ProxyCatalogStore: Load nodes");
                }
            }

            return new List<ProxyNode>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadProxyDefinitions()
    {
        lock (_sync)
        {
            foreach (var yaml in AtomicFile.ReadTextCandidates(_proxiesPath))
            {
                try
                {
                    return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                           ?? new List<Dictionary<string, object>>();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "ProxyCatalogStore: Load proxy definitions");
                }
            }

            return new List<Dictionary<string, object>>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadProxyGroups()
    {
        lock (_sync)
        {
            foreach (var yaml in AtomicFile.ReadTextCandidates(_groupsPath))
            {
                try
                {
                    return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                           ?? new List<Dictionary<string, object>>();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "ProxyCatalogStore: Load proxy groups");
                }
            }

            return new List<Dictionary<string, object>>();
        }
    }

    public IReadOnlyDictionary<string, object> LoadRuleProviders()
    {
        lock (_sync)
        {
            foreach (var yaml in AtomicFile.ReadTextCandidates(_ruleProvidersPath))
            {
                try
                {
                    using var reader = new StringReader(yaml);
                    var deserialized = _deserializer.Deserialize<object>(reader);
                    return NormalizeMapping(deserialized) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "ProxyCatalogStore: Load rule providers");
                }
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> LoadRules()
    {
        lock (_sync)
        {
            foreach (var text in AtomicFile.ReadTextCandidates(_rulesPath))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    return text
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Trim())
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                        .ToList();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "ProxyCatalogStore: Load rules");
                }
            }

            return new List<string>();
        }
    }

    public void Save(
        IEnumerable<ProxyNode> nodes,
        IEnumerable<Dictionary<string, object>> proxies,
        IEnumerable<Dictionary<string, object>> groups,
        IDictionary<string, object>? ruleProviders = null,
        IEnumerable<string>? rules = null)
    {
        lock (_sync)
        {
            var utf8NoBom = new UTF8Encoding(false);

            var json = JsonSerializer.Serialize(nodes, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            AtomicFile.WriteAllText(_nodesPath, json, utf8NoBom);

            var yaml = _serializer.Serialize(proxies);
            AtomicFile.WriteAllText(_proxiesPath, yaml, utf8NoBom);

            var groupsYaml = _serializer.Serialize(groups);
            AtomicFile.WriteAllText(_groupsPath, groupsYaml, utf8NoBom);

            var ruleProvidersYaml = _serializer.Serialize(ruleProviders ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
            AtomicFile.WriteAllText(_ruleProvidersPath, ruleProvidersYaml, utf8NoBom);

            var rulesText = string.Join(Environment.NewLine, rules ?? Array.Empty<string>());
            AtomicFile.WriteAllText(_rulesPath, rulesText, utf8NoBom);
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
}
