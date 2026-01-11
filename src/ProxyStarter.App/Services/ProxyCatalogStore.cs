using System.Collections.Generic;
using System.IO;
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
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public ProxyCatalogStore()
    {
        _nodesPath = Path.Combine(AppPaths.DataDirectory, "nodes.json");
        _proxiesPath = Path.Combine(AppPaths.DataDirectory, "proxies.yaml");
        _groupsPath = Path.Combine(AppPaths.DataDirectory, "groups.yaml");
        _serializer = new SerializerBuilder().Build();
        _deserializer = new DeserializerBuilder().Build();
    }

    public IReadOnlyList<ProxyNode> LoadNodes()
    {
        try
        {
            if (!File.Exists(_nodesPath))
            {
                return new List<ProxyNode>();
            }

            var json = File.ReadAllText(_nodesPath);
            return JsonSerializer.Deserialize<List<ProxyNode>>(json) ?? new List<ProxyNode>();
        }
        catch
        {
            return new List<ProxyNode>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadProxyDefinitions()
    {
        try
        {
            if (!File.Exists(_proxiesPath))
            {
                return new List<Dictionary<string, object>>();
            }

            var yaml = File.ReadAllText(_proxiesPath);
            return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                   ?? new List<Dictionary<string, object>>();
        }
        catch
        {
            return new List<Dictionary<string, object>>();
        }
    }

    public IReadOnlyList<Dictionary<string, object>> LoadProxyGroups()
    {
        try
        {
            if (!File.Exists(_groupsPath))
            {
                return new List<Dictionary<string, object>>();
            }

            var yaml = File.ReadAllText(_groupsPath);
            return _deserializer.Deserialize<List<Dictionary<string, object>>>(yaml)
                   ?? new List<Dictionary<string, object>>();
        }
        catch
        {
            return new List<Dictionary<string, object>>();
        }
    }

    public void Save(
        IEnumerable<ProxyNode> nodes,
        IEnumerable<Dictionary<string, object>> proxies,
        IEnumerable<Dictionary<string, object>> groups)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var utf8NoBom = new UTF8Encoding(false);

        var json = JsonSerializer.Serialize(nodes, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_nodesPath, json, utf8NoBom);

        var yaml = _serializer.Serialize(proxies);
        File.WriteAllText(_proxiesPath, yaml, utf8NoBom);

        var groupsYaml = _serializer.Serialize(groups);
        File.WriteAllText(_groupsPath, groupsYaml, utf8NoBom);
    }
}
