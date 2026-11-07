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
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private readonly object _sync = new();
    public ProxyCatalogStore()
    {
        _nodesPath = Path.Combine(AppPaths.DataDirectory, "nodes.json");
        _proxiesPath = Path.Combine(AppPaths.DataDirectory, "proxies.yaml");
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

    internal (IReadOnlyList<ProxyNode> Nodes, IReadOnlyList<Dictionary<string, object>> Proxies) LoadOutbounds()
    {
        lock (_sync)
        {
            return (LoadNodes(), LoadProxyDefinitions());
        }
    }

    public void Save(
        IReadOnlyList<ProxyNode> nodes,
        IEnumerable<Dictionary<string, object>> proxies)
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
        }
    }
}
