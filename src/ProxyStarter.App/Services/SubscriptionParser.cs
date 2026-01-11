using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProxyStarter.App.Models;
using YamlDotNet.RepresentationModel;

namespace ProxyStarter.App.Services;

public sealed class SubscriptionParser
{
    public SubscriptionParseResult Parse(string content, string sourceName, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new SubscriptionParseResult();
        }

        var trimmed = content.Trim().TrimStart('\uFEFF');
        if (LooksLikeYaml(trimmed))
        {
            return ParseYaml(trimmed, sourceId);
        }

        var decoded = DecodeBase64(trimmed).TrimStart('\uFEFF');
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            if (LooksLikeYaml(decoded))
            {
                return ParseYaml(decoded, sourceId);
            }

            if (decoded.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                return ParseLinkLines(decoded, sourceName, sourceId);
            }
        }

        return ParseLinkLines(trimmed, sourceName, sourceId);
    }

    private SubscriptionParseResult ParseLinkLines(string content, string sourceName, string sourceId)
    {
        var lines = ExtractLines(content);
        var proxies = new List<Dictionary<string, object>>();
        var nodes = new List<ProxyNode>();
        var index = 1;

        foreach (var line in lines)
        {
            if (TryParseLink(line, sourceName, index, out var proxy, out var node))
            {
                if (!string.IsNullOrWhiteSpace(sourceId))
                {
                    node.SourceId = sourceId;
                }

                proxies.Add(proxy);
                nodes.Add(node);
                index++;
            }
        }

        return new SubscriptionParseResult
        {
            Proxies = proxies,
            ProxyGroups = new List<Dictionary<string, object>>(),
            Nodes = nodes
        };
    }

    private static SubscriptionParseResult ParseYaml(string content, string sourceId)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                return new SubscriptionParseResult();
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return new SubscriptionParseResult();
            }

            var proxies = new List<Dictionary<string, object>>();
            var nodes = new List<ProxyNode>();
            var groups = new List<Dictionary<string, object>>();

            if (TryGetChild(mapping, "proxies", out var proxiesNode) && proxiesNode is YamlSequenceNode sequence)
            {
                foreach (var node in sequence.Children.OfType<YamlMappingNode>())
                {
                    var proxy = ConvertMapping(node);
                    if (proxy.Count == 0)
                    {
                        continue;
                    }

                    proxies.Add(proxy);
                    var proxyNode = BuildProxyNode(proxy);
                    if (proxyNode is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(sourceId))
                        {
                            proxyNode.SourceId = sourceId;
                        }

                        nodes.Add(proxyNode);
                    }
                }
            }

            if (TryGetChild(mapping, "proxy-groups", out var groupsNode) && groupsNode is YamlSequenceNode groupsSequence)
            {
                foreach (var groupNode in groupsSequence.Children.OfType<YamlMappingNode>())
                {
                    var group = ConvertMapping(groupNode);
                    if (group.Count == 0)
                    {
                        continue;
                    }

                    groups.Add(group);
                }
            }

            return new SubscriptionParseResult
            {
                Proxies = proxies,
                ProxyGroups = groups,
                Nodes = nodes
            };
        }
        catch
        {
            return new SubscriptionParseResult();
        }
    }

    private static bool TryParseLink(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseShadowsocks(line, sourceName, index, out proxy, out node);
        }

        if (line.StartsWith("ssr://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseShadowsocksR(line, sourceName, index, out proxy, out node);
        }

        if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseTrojan(line, sourceName, index, out proxy, out node);
        }

        if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseVless(line, sourceName, index, out proxy, out node);
        }

        if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseVmess(line, sourceName, index, out proxy, out node);
        }

        return false;
    }

    private static bool TryParseShadowsocks(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var payload = line.Substring(5);
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out _);

        string decoded;
        if (payload.Contains('@'))
        {
            decoded = payload;
        }
        else
        {
            decoded = DecodeBase64(payload);
        }

        var atIndex = decoded.LastIndexOf('@');
        if (atIndex <= 0)
        {
            return false;
        }

        var userInfo = decoded[..atIndex];
        var hostInfo = decoded[(atIndex + 1)..];
        var parts = userInfo.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var hostParts = hostInfo.Split(':', 2);
        if (hostParts.Length != 2 || !int.TryParse(hostParts[1], out var port))
        {
            return false;
        }

        name = NormalizeName(name, sourceName, index, hostParts[0]);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "ss",
            ["server"] = hostParts[0],
            ["port"] = port,
            ["cipher"] = parts[0],
            ["password"] = parts[1],
            ["udp"] = true
        };

        node = new ProxyNode
        {
            Name = name,
            Type = "ss",
            Address = hostParts[0],
            Port = port
        };

        return true;
    }

    private static bool TryParseShadowsocksR(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var payload = line.Substring(6);
        var decoded = DecodeBase64(payload);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        var parts = decoded.Split("/?", 2, StringSplitOptions.None);
        var mainParts = parts[0].Split(':');
        if (mainParts.Length < 6 || !int.TryParse(mainParts[1], out var port))
        {
            return false;
        }

        var password = DecodeBase64(mainParts[5]);
        var query = parts.Length > 1 ? parts[1] : string.Empty;
        var parameters = ParseQuery(query);
        var name = parameters.TryGetValue("remarks", out var remarks) ? DecodeBase64(remarks) : string.Empty;
        name = NormalizeName(name, sourceName, index, mainParts[0]);

        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "ssr",
            ["server"] = mainParts[0],
            ["port"] = port,
            ["protocol"] = mainParts[2],
            ["cipher"] = mainParts[3],
            ["obfs"] = mainParts[4],
            ["password"] = password,
            ["udp"] = true
        };

        if (parameters.TryGetValue("obfsparam", out var obfsParam))
        {
            proxy["obfs-param"] = DecodeBase64(obfsParam);
        }

        if (parameters.TryGetValue("protoparam", out var protoParam))
        {
            proxy["protocol-param"] = DecodeBase64(protoParam);
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "ssr",
            Address = mainParts[0],
            Port = port
        };

        return true;
    }

    private static bool TryParseTrojan(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var name = NormalizeName(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')), sourceName, index, uri.Host);
        var query = ParseQuery(uri.Query);

        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "trojan",
            ["server"] = uri.Host,
            ["port"] = uri.Port,
            ["password"] = Uri.UnescapeDataString(uri.UserInfo),
            ["udp"] = true
        };

        if (query.TryGetValue("sni", out var sni))
        {
            proxy["sni"] = sni;
        }
        else if (query.TryGetValue("peer", out var peer))
        {
            proxy["sni"] = peer;
        }
        if (query.TryGetValue("allowInsecure", out var allow) && IsTrue(allow))
        {
            proxy["skip-cert-verify"] = true;
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "trojan",
            Address = uri.Host,
            Port = uri.Port
        };

        return true;
    }

    private static bool TryParseVless(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var name = NormalizeName(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')), sourceName, index, uri.Host);
        var query = ParseQuery(uri.Query);
        var network = query.TryGetValue("type", out var type) ? type : "tcp";
        var security = query.TryGetValue("security", out var sec) ? sec : string.Empty;

        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "vless",
            ["server"] = uri.Host,
            ["port"] = uri.Port,
            ["uuid"] = Uri.UnescapeDataString(uri.UserInfo),
            ["udp"] = true,
            ["network"] = network
        };

        if (string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase))
        {
            proxy["tls"] = true;
        }

        if (query.TryGetValue("sni", out var sni))
        {
            proxy["sni"] = sni;
        }

        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = new Dictionary<string, object>();
            if (query.TryGetValue("path", out var path))
            {
                wsOpts["path"] = path;
            }
            if (query.TryGetValue("host", out var host))
            {
                wsOpts["headers"] = new Dictionary<string, object>
                {
                    ["Host"] = host
                };
            }
            if (wsOpts.Count > 0)
            {
                proxy["ws-opts"] = wsOpts;
            }
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "vless",
            Address = uri.Host,
            Port = uri.Port
        };

        return true;
    }

    private static bool TryParseVmess(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var payload = line.Substring(8);
        var decoded = DecodeBase64(payload);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        using var document = JsonDocument.Parse(decoded);
        var root = document.RootElement;

        var name = root.TryGetProperty("ps", out var psElement) ? psElement.GetString() : string.Empty;
        var server = root.TryGetProperty("add", out var addElement) ? addElement.GetString() : string.Empty;
        var portText = root.TryGetProperty("port", out var portElement)
            ? portElement.ValueKind == JsonValueKind.Number ? portElement.GetInt32().ToString(CultureInfo.InvariantCulture) : portElement.GetString()
            : string.Empty;
        var uuid = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : string.Empty;
        var alterIdText = root.TryGetProperty("aid", out var aidElement)
            ? aidElement.ValueKind == JsonValueKind.Number ? aidElement.GetInt32().ToString(CultureInfo.InvariantCulture) : aidElement.GetString()
            : "0";
        var network = root.TryGetProperty("net", out var netElement) ? netElement.GetString() : "tcp";
        var tls = root.TryGetProperty("tls", out var tlsElement) ? tlsElement.GetString() : string.Empty;
        var host = root.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : string.Empty;
        var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : string.Empty;

        if (!int.TryParse(portText, out var port) || string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uuid))
        {
            return false;
        }

        name = NormalizeName(name ?? string.Empty, sourceName, index, server ?? string.Empty);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "vmess",
            ["server"] = server ?? string.Empty,
            ["port"] = port,
            ["uuid"] = uuid ?? string.Empty,
            ["alterId"] = int.TryParse(alterIdText, out var alterId) ? alterId : 0,
            ["cipher"] = "auto",
            ["udp"] = true,
            ["network"] = network ?? "tcp"
        };

        if (string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase))
        {
            proxy["tls"] = true;
        }

        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(path))
            {
                wsOpts["path"] = path;
            }
            if (!string.IsNullOrWhiteSpace(host))
            {
                wsOpts["headers"] = new Dictionary<string, object>
                {
                    ["Host"] = host
                };
            }
            if (wsOpts.Count > 0)
            {
                proxy["ws-opts"] = wsOpts;
            }
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "vmess",
            Address = server ?? string.Empty,
            Port = port
        };

        return true;
    }

    private static ProxyNode? BuildProxyNode(Dictionary<string, object> proxy)
    {
        if (!proxy.TryGetValue("name", out var nameObj) || !proxy.TryGetValue("type", out var typeObj))
        {
            return null;
        }

        var name = nameObj.ToString() ?? string.Empty;
        var type = typeObj.ToString() ?? string.Empty;
        var server = proxy.TryGetValue("server", out var serverObj) ? serverObj.ToString() ?? string.Empty : string.Empty;
        var port = proxy.TryGetValue("port", out var portObj) && int.TryParse(portObj.ToString(), out var portValue)
            ? portValue
            : 0;

        return new ProxyNode
        {
            Name = name,
            Type = type,
            Address = server,
            Port = port
        };
    }

    private static bool LooksLikeYaml(string content)
    {
        return content.Contains("proxies:", StringComparison.OrdinalIgnoreCase)
               || content.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractLines(string content)
    {
        if (content.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            return SplitLines(content);
        }

        var decoded = DecodeBase64(content.Trim());
        if (!string.IsNullOrWhiteSpace(decoded) && decoded.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            return SplitLines(decoded);
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        return content
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string ExtractFragment(string payload, out string name)
    {
        name = string.Empty;
        var hashIndex = payload.IndexOf('#');
        if (hashIndex < 0)
        {
            return payload;
        }

        name = Uri.UnescapeDataString(payload[(hashIndex + 1)..]);
        return payload[..hashIndex];
    }

    private static string ExtractQuery(string payload, out string query)
    {
        query = string.Empty;
        var queryIndex = payload.IndexOf('?');
        if (queryIndex < 0)
        {
            return payload;
        }

        query = payload[(queryIndex + 1)..];
        return payload[..queryIndex];
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.TrimStart('?');
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string NormalizeName(string? name, string sourceName, int index, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return $"{sourceName}-{index}";
    }

    private static string DecodeBase64(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.Trim()
            .Replace('-', '+')
            .Replace('_', '/');

        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch
        {
            return string.Empty;
        }
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

    private static Dictionary<string, object> ConvertMapping(YamlMappingNode node)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in node.Children)
        {
            if (child.Key is not YamlScalarNode keyNode || keyNode.Value is null)
            {
                continue;
            }

            result[keyNode.Value] = ConvertYamlNode(child.Value) ?? string.Empty;
        }

        return result;
    }

    private static object? ConvertYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return ConvertScalar(scalar.Value);
            case YamlSequenceNode sequence:
                return sequence.Children.Select(ConvertYamlNode).Where(item => item is not null).ToList();
            case YamlMappingNode mapping:
                var dict = new Dictionary<string, object>();
                foreach (var child in mapping.Children)
                {
                    if (child.Key is YamlScalarNode key && key.Value is not null)
                    {
                        dict[key.Value] = ConvertYamlNode(child.Value) ?? string.Empty;
                    }
                }
                return dict;
            default:
                return null;
        }
    }

    private static object? ConvertScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return value;
    }

    private static bool IsTrue(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
