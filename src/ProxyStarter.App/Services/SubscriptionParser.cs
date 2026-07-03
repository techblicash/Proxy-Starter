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
            var ruleProviders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var rules = new List<string>();

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

            if (TryGetChild(mapping, "rule-providers", out var providersNode) && providersNode is YamlMappingNode providersMapping)
            {
                foreach (var provider in providersMapping.Children)
                {
                    if (provider.Key is not YamlScalarNode keyNode || string.IsNullOrWhiteSpace(keyNode.Value))
                    {
                        continue;
                    }

                    ruleProviders[keyNode.Value] = ConvertYamlNode(provider.Value) ?? string.Empty;
                }
            }

            if (TryGetChild(mapping, "rules", out var rulesNode) && rulesNode is YamlSequenceNode rulesSequence)
            {
                foreach (var ruleNode in rulesSequence.Children)
                {
                    var ruleText = ruleNode switch
                    {
                        YamlScalarNode scalar => scalar.Value,
                        _ => ConvertYamlNode(ruleNode)?.ToString()
                    };

                    if (!string.IsNullOrWhiteSpace(ruleText))
                    {
                        rules.Add(ruleText.Trim());
                    }
                }
            }

            return new SubscriptionParseResult
            {
                Proxies = proxies,
                ProxyGroups = groups,
                RuleProviders = ruleProviders,
                Rules = rules,
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

        try
        {
            if (line.StartsWith("v2rayn://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseV2RayN(line, sourceName, index, out proxy, out node);
            }

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

            if (line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseHysteria2(line, sourceName, index, out proxy, out node);
            }

            if (line.StartsWith("hysteria://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("hy://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseHysteria(line, sourceName, index, out proxy, out node);
            }

            if (line.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseTuic(line, sourceName, index, out proxy, out node);
            }

            if (line.StartsWith("anytls://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseAnyTls(line, sourceName, index, out proxy, out node);
            }

            if (line.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseStandardProxy(line, sourceName, index, out proxy, out node);
            }
        }
        catch
        {
            proxy = new Dictionary<string, object>();
            node = new ProxyNode();
            return false;
        }

        return false;
    }

    private static bool TryParseShadowsocks(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var payload = line.Substring(5);
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out var queryText);
        var query = ParseQuery(queryText);

        string method;
        string password;
        string server;
        int port;

        if (payload.Contains('@', StringComparison.Ordinal))
        {
            if (!TryParseAuthority(payload, out var userInfo, out server, out var portText)
                || !TryParsePort(portText, out port))
            {
                return false;
            }

            userInfo = Uri.UnescapeDataString(userInfo);
            var userParts = userInfo.Split(':', 2);
            if (userParts.Length != 2)
            {
                userInfo = DecodeBase64(userInfo);
                userParts = userInfo.Split(':', 2);
            }

            if (userParts.Length != 2)
            {
                return false;
            }

            method = userParts[0];
            password = userParts[1];
        }
        else
        {
            var decoded = DecodeBase64(payload);
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

            if (!TryParseAuthority(hostInfo, out _, out server, out var portText)
                || !TryParsePort(portText, out port))
            {
                return false;
            }

            method = parts[0];
            password = parts[1];
        }

        name = NormalizeName(name, sourceName, index, server);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "ss",
            ["server"] = server,
            ["port"] = port,
            ["cipher"] = method,
            ["password"] = password,
            ["udp"] = true
        };

        AddOptionalString(proxy, query, "plugin", "plugin");

        node = new ProxyNode
        {
            Name = name,
            Type = "ss",
            Address = server,
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
            ["udp"] = true,
            ["network"] = query.TryGetValue("type", out var network) ? network : "tcp",
            ["security"] = query.TryGetValue("security", out var security) ? security : "tls",
            ["tls"] = !query.TryGetValue("security", out var trojanSecurity)
                      || !trojanSecurity.Equals("none", StringComparison.OrdinalIgnoreCase)
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

        AddTransportOptions(proxy, query);

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
            ["network"] = network,
            ["security"] = security
        };

        if (string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase)
            || string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase))
        {
            proxy["tls"] = true;
        }

        if (query.TryGetValue("sni", out var sni))
        {
            proxy["sni"] = sni;
            proxy["servername"] = sni;
        }

        AddOptionalString(proxy, query, "flow", "flow");
        AddOptionalString(proxy, query, "fp", "client-fingerprint");

        if (string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase))
        {
            var realityOptions = new Dictionary<string, object>();
            if (query.TryGetValue("pbk", out var publicKey))
            {
                realityOptions["public-key"] = publicKey;
            }

            if (query.TryGetValue("sid", out var shortId))
            {
                realityOptions["short-id"] = shortId;
            }

            if (realityOptions.Count > 0)
            {
                proxy["reality-opts"] = realityOptions;
            }
        }

        AddTransportOptions(proxy, query);

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
        var sni = root.TryGetProperty("sni", out var sniElement) ? sniElement.GetString() : string.Empty;
        var security = root.TryGetProperty("scy", out var securityElement)
            ? securityElement.GetString()
            : root.TryGetProperty("security", out var legacySecurityElement)
                ? legacySecurityElement.GetString()
                : "auto";

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
            ["cipher"] = string.IsNullOrWhiteSpace(security) ? "auto" : security,
            ["udp"] = true,
            ["network"] = network ?? "tcp"
        };

        if (string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase))
        {
            proxy["tls"] = true;
        }

        if (!string.IsNullOrWhiteSpace(sni))
        {
            proxy["sni"] = sni;
            proxy["servername"] = sni;
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

    private static bool TryParseHysteria2(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var payload = line[(schemeEnd + 3)..];
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out var queryText);

        var query = ParseQuery(queryText);
        var authority = payload.Split('/', 2)[0];
        if (!TryParseAuthority(authority, out var auth, out var server, out var portText))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(auth))
        {
            if (query.TryGetValue("auth", out var queryAuth))
            {
                auth = queryAuth;
            }
            else if (query.TryGetValue("password", out var queryPassword))
            {
                auth = queryPassword;
            }
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(auth))
        {
            return false;
        }

        name = NormalizeName(name, sourceName, index, server);
        var port = TryParsePort(portText, out var parsedPort) ? parsedPort : ExtractFirstPort(portText, 443);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "hysteria2",
            ["server"] = server,
            ["port"] = port,
            ["password"] = auth,
            ["udp"] = true
        };

        if (!TryParsePort(portText, out _) && !string.IsNullOrWhiteSpace(portText))
        {
            proxy["ports"] = portText;
        }

        if (query.TryGetValue("ports", out var ports) && !string.IsNullOrWhiteSpace(ports))
        {
            proxy["ports"] = ports;
        }

        AddOptionalString(proxy, query, "sni", "sni");
        AddOptionalString(proxy, query, "obfs", "obfs");
        AddOptionalString(proxy, query, "obfs-password", "obfs-password");
        AddOptionalString(proxy, query, "pinSHA256", "fingerprint");
        AddOptionalString(proxy, query, "fingerprint", "fingerprint");
        AddOptionalString(proxy, query, "up", "up");
        AddOptionalString(proxy, query, "down", "down");

        if (query.TryGetValue("hop-interval", out var hopInterval) && !string.IsNullOrWhiteSpace(hopInterval))
        {
            proxy["hop-interval"] = TryParseDurationSeconds(hopInterval, out var hopIntervalValue)
                ? hopIntervalValue
                : hopInterval;
        }

        if (query.TryGetValue("insecure", out var insecure) && IsTrue(insecure))
        {
            proxy["skip-cert-verify"] = true;
        }
        else if (query.TryGetValue("allowInsecure", out var allowInsecure) && IsTrue(allowInsecure))
        {
            proxy["skip-cert-verify"] = true;
        }
        else if (query.TryGetValue("skip-cert-verify", out var skipCertVerify) && IsTrue(skipCertVerify))
        {
            proxy["skip-cert-verify"] = true;
        }

        if (query.TryGetValue("alpn", out var alpn) && !string.IsNullOrWhiteSpace(alpn))
        {
            var alpnValues = alpn
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (alpnValues.Count > 0)
            {
                proxy["alpn"] = alpnValues;
            }
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "hysteria2",
            Address = server,
            Port = port
        };

        return true;
    }

    private static bool TryParseHysteria(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var payload = line[(schemeEnd + 3)..];
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out var queryText);

        var query = ParseQuery(queryText);
        var authority = payload.Split('/', 2)[0];
        if (!TryParseAuthority(authority, out var auth, out var server, out var portText)
            || !TryParsePort(portText, out var port))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(auth))
        {
            auth = GetFirstQueryValue(query, "auth", "auth_str", "auth-str", "password");
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(auth))
        {
            return false;
        }

        name = NormalizeName(name, sourceName, index, server);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "hysteria",
            ["server"] = server,
            ["port"] = port,
            ["auth-str"] = auth,
            ["protocol"] = DefaultIfBlank(GetFirstQueryValue(query, "protocol"), "udp"),
            ["udp"] = true
        };

        AddOptionalString(proxy, query, "peer", "sni");
        AddOptionalString(proxy, query, "sni", "sni");
        AddOptionalString(proxy, query, "obfs", "obfs");
        AddOptionalString(proxy, query, "alpn", "alpn");
        AddOptionalString(proxy, query, "upmbps", "up");
        AddOptionalString(proxy, query, "downmbps", "down");
        AddOptionalString(proxy, query, "up", "up");
        AddOptionalString(proxy, query, "down", "down");

        if ((query.TryGetValue("insecure", out var insecure) && IsTrue(insecure))
            || (query.TryGetValue("allowInsecure", out var allowInsecure) && IsTrue(allowInsecure))
            || (query.TryGetValue("skip-cert-verify", out var skipCertVerify) && IsTrue(skipCertVerify)))
        {
            proxy["skip-cert-verify"] = true;
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "hysteria",
            Address = server,
            Port = port
        };

        return true;
    }

    private static bool TryParseTuic(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var payload = line[(schemeEnd + 3)..];
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out var queryText);

        var query = ParseQuery(queryText);
        var authority = payload.Split('/', 2)[0];
        if (!TryParseAuthority(authority, out var auth, out var server, out var portText)
            || !TryParsePort(portText, out var port))
        {
            return false;
        }

        var authParts = auth.Split(':', 2);
        var uuid = authParts.Length > 0 ? authParts[0] : string.Empty;
        var password = authParts.Length > 1 ? authParts[1] : string.Empty;
        uuid = DefaultIfBlank(uuid, GetFirstQueryValue(query, "uuid", "username", "user"));
        password = DefaultIfBlank(password, GetFirstQueryValue(query, "password", "token"));

        if (string.IsNullOrWhiteSpace(server)
            || string.IsNullOrWhiteSpace(uuid)
            || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        name = NormalizeName(name, sourceName, index, server);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "tuic",
            ["server"] = server,
            ["port"] = port,
            ["uuid"] = uuid,
            ["password"] = password,
            ["udp"] = true
        };

        AddOptionalString(proxy, query, "sni", "sni");
        AddOptionalString(proxy, query, "congestion_control", "congestion-controller");
        AddOptionalString(proxy, query, "congestion-controller", "congestion-controller");
        AddOptionalString(proxy, query, "udp_relay_mode", "udp-relay-mode");
        AddOptionalString(proxy, query, "udp-relay-mode", "udp-relay-mode");
        AddAlpn(proxy, GetFirstQueryValue(query, "alpn"));

        if ((query.TryGetValue("insecure", out var insecure) && IsTrue(insecure))
            || (query.TryGetValue("allowInsecure", out var allowInsecure) && IsTrue(allowInsecure))
            || (query.TryGetValue("allow_insecure", out var allowInsecureSnake) && IsTrue(allowInsecureSnake))
            || (query.TryGetValue("skip-cert-verify", out var skipCertVerify) && IsTrue(skipCertVerify)))
        {
            proxy["skip-cert-verify"] = true;
        }

        if (query.TryGetValue("disable_sni", out var disableSni) && IsTrue(disableSni))
        {
            proxy["disable-sni"] = true;
        }

        if (query.TryGetValue("reduce_rtt", out var reduceRtt) && IsTrue(reduceRtt))
        {
            proxy["reduce-rtt"] = true;
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "tuic",
            Address = server,
            Port = port
        };

        return true;
    }

    private static bool TryParseAnyTls(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var payload = line[(schemeEnd + 3)..];
        payload = ExtractFragment(payload, out var name);
        payload = ExtractQuery(payload, out var queryText);

        var query = ParseQuery(queryText);
        var authority = payload.Split('/', 2)[0];
        if (!TryParseAuthority(authority, out var password, out var server, out var portText)
            || !TryParsePort(portText, out var port))
        {
            return false;
        }

        password = DefaultIfBlank(password, GetFirstQueryValue(query, "password"));
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        name = NormalizeName(name, sourceName, index, server);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "anytls",
            ["server"] = server,
            ["port"] = port,
            ["password"] = password,
            ["udp"] = true
        };

        AddOptionalString(proxy, query, "sni", "sni");
        AddOptionalString(proxy, query, "fp", "client-fingerprint");
        AddOptionalString(proxy, query, "fingerprint", "client-fingerprint");
        AddAlpn(proxy, GetFirstQueryValue(query, "alpn"));

        if ((query.TryGetValue("insecure", out var insecure) && IsTrue(insecure))
            || (query.TryGetValue("allowInsecure", out var allowInsecure) && IsTrue(allowInsecure))
            || (query.TryGetValue("skip-cert-verify", out var skipCertVerify) && IsTrue(skipCertVerify)))
        {
            proxy["skip-cert-verify"] = true;
        }

        node = new ProxyNode
        {
            Name = name,
            Type = "anytls",
            Address = server,
            Port = port
        };

        return true;
    }

    private static bool TryParseStandardProxy(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var scheme = uri.Scheme.Trim().ToLowerInvariant();
        var type = scheme switch
        {
            "socks" => "socks5",
            "socks5" => "socks5",
            "socks5h" => "socks5",
            "http" => "http",
            "https" => "http",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var port = uri.IsDefaultPort
            ? scheme == "https" ? 443 : type == "socks5" ? 1080 : 80
            : uri.Port;
        if (!TryParsePort(port.ToString(CultureInfo.InvariantCulture), out port))
        {
            return false;
        }

        var name = NormalizeName(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')), sourceName, index, uri.Host);
        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = type,
            ["server"] = uri.Host,
            ["port"] = port
        };

        if (scheme == "https")
        {
            proxy["tls"] = true;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var userInfo = Uri.UnescapeDataString(uri.UserInfo);
            var parts = userInfo.Split(':', 2);
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                proxy["username"] = parts[0];
            }

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                proxy["password"] = parts[1];
            }
        }

        node = new ProxyNode
        {
            Name = name,
            Type = type,
            Address = uri.Host,
            Port = port
        };

        return true;
    }

    private static bool TryParseV2RayN(string line, string sourceName, int index, out Dictionary<string, object> proxy, out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        var payload = line["v2rayn://".Length..];
        payload = ExtractFragment(payload, out _);
        payload = ExtractQuery(payload, out _);

        var slashIndex = payload.IndexOf('/');
        var rawType = slashIndex > 0 ? payload[..slashIndex] : string.Empty;
        var encoded = slashIndex > 0 ? payload[(slashIndex + 1)..] : payload;
        var decoded = DecodeBase64(encoded);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        using var document = JsonDocument.Parse(decoded);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryBuildV2RayNProxy(root, rawType, sourceName, index, out proxy, out node);
    }

    private static bool TryBuildV2RayNProxy(
        JsonElement root,
        string rawType,
        string sourceName,
        int index,
        out Dictionary<string, object> proxy,
        out ProxyNode node)
    {
        proxy = new Dictionary<string, object>();
        node = new ProxyNode();

        TryGetJsonObject(root, out var extra, "ProtoExtraObj", "ProtoExtra", "Extra", "ExtraObj");

        var type = NormalizeV2RayNType(rawType, GetJsonString(root, "ConfigType"));
        var server = GetJsonString(root, "Address", "Server", "server", "add");
        var port = GetJsonInt(root, "Port", "server_port", "port");
        var name = NormalizeName(GetJsonString(root, "Remarks", "Remark", "Name", "ps"), sourceName, index, server);
        var network = NormalizeNetwork(GetJsonString(root, "Network", "Net", "network", "net"));
        var security = GetJsonString(root, "StreamSecurity", "Security", "TLS", "Tls", "tls");
        var sni = GetJsonString(root, extra, "Sni", "SNI", "ServerName", "Peer", "sni");
        var allowInsecure = GetJsonBool(root, "AllowInsecure", "Insecure", "SkipCertVerify", "skip-cert-verify", "allowInsecure");

        if (string.IsNullOrWhiteSpace(type)
            || string.IsNullOrWhiteSpace(server)
            || !IsValidPortNumber(port))
        {
            return false;
        }

        proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = type,
            ["server"] = server,
            ["port"] = port,
            ["udp"] = true
        };

        switch (type)
        {
            case "vmess":
                var vmessUuid = GetJsonString(root, "Id", "ID", "Uuid", "UUID", "UserId", "AlterIdUserId");
                if (string.IsNullOrWhiteSpace(vmessUuid))
                {
                    return false;
                }

                proxy["uuid"] = vmessUuid;
                proxy["alterId"] = GetJsonInt(root, "AlterId", "AlterID", "Aid", "aid");
                proxy["cipher"] = DefaultIfBlank(GetJsonString(root, "Security", "Cipher", "Scy", "scy"), "auto");
                proxy["network"] = network;
                ApplyTlsOptions(proxy, security, sni, allowInsecure);
                AddJsonTransportOptions(proxy, root, extra);
                break;

            case "vless":
                var vlessUuid = GetJsonString(root, "Id", "ID", "Uuid", "UUID", "UserId", "Password", "Username");
                if (string.IsNullOrWhiteSpace(vlessUuid))
                {
                    return false;
                }

                proxy["uuid"] = vlessUuid;
                proxy["network"] = network;
                proxy["security"] = DefaultIfBlank(security, "none").ToLowerInvariant();
                AddOptionalJsonString(proxy, root, "flow", "Flow", "flow");
                ApplyTlsOptions(proxy, security, sni, allowInsecure);
                AddOptionalJsonString(proxy, root, "client-fingerprint", "Fingerprint", "fingerprint", "fp");
                AddRealityOptions(proxy, root, extra);
                AddJsonTransportOptions(proxy, root, extra);
                break;

            case "trojan":
                var trojanPassword = GetJsonString(root, "Password", "password");
                if (string.IsNullOrWhiteSpace(trojanPassword))
                {
                    return false;
                }

                proxy["password"] = trojanPassword;
                proxy["network"] = network;
                proxy["security"] = DefaultIfBlank(security, "tls").ToLowerInvariant();
                proxy["tls"] = true;
                ApplyTlsOptions(proxy, "tls", sni, allowInsecure);
                AddJsonTransportOptions(proxy, root, extra);
                break;

            case "ss":
                var cipher = GetJsonString(root, "Method", "Cipher", "Security", "scy");
                var ssPassword = GetJsonString(root, "Password", "password");
                if (string.IsNullOrWhiteSpace(cipher) || string.IsNullOrWhiteSpace(ssPassword))
                {
                    return false;
                }

                proxy["cipher"] = cipher;
                proxy["password"] = ssPassword;
                break;

            case "hysteria":
                var hysteriaAuth = GetJsonString(root, "Auth", "AuthStr", "Password", "password");
                if (string.IsNullOrWhiteSpace(hysteriaAuth))
                {
                    return false;
                }

                proxy["auth-str"] = hysteriaAuth;
                proxy["protocol"] = DefaultIfBlank(GetJsonString(root, extra, "Protocol", "protocol"), "udp");
                ApplySni(proxy, sni);
                AddHysteriaSpeeds(proxy, root, extra);
                AddOptionalJsonString(proxy, root, extra, "obfs", "Obfs", "obfs");
                if (allowInsecure)
                {
                    proxy["skip-cert-verify"] = true;
                }
                break;

            case "hysteria2":
                var hysteria2Password = GetJsonString(root, "Password", "Auth", "AuthStr", "password");
                if (string.IsNullOrWhiteSpace(hysteria2Password))
                {
                    return false;
                }

                proxy["password"] = hysteria2Password;
                ApplySni(proxy, sni);
                AddHysteriaSpeeds(proxy, root, extra);
                AddOptionalJsonString(proxy, root, extra, "ports", "Ports", "ports");
                AddHysteriaHopInterval(proxy, root, extra);
                AddOptionalJsonString(proxy, root, extra, "obfs", "Obfs", "obfs");
                AddOptionalJsonString(proxy, root, extra, "obfs-password", "ObfsPassword", "obfs-password", "obfs_password");
                AddAlpn(proxy, GetJsonString(root, extra, "Alpn", "ALPN", "alpn"));
                if (allowInsecure)
                {
                    proxy["skip-cert-verify"] = true;
                }
                break;

            case "tuic":
                var tuicUuid = GetJsonString(root, "Username", "UserName", "Uuid", "UUID", "Id", "ID");
                var tuicPassword = GetJsonString(root, "Password", "password");
                if (string.IsNullOrWhiteSpace(tuicUuid) || string.IsNullOrWhiteSpace(tuicPassword))
                {
                    return false;
                }

                proxy["uuid"] = tuicUuid;
                proxy["password"] = tuicPassword;
                ApplySni(proxy, sni);
                AddAlpn(proxy, GetJsonString(root, extra, "Alpn", "ALPN", "alpn"));
                AddOptionalJsonString(proxy, root, extra, "congestion-controller", "CongestionControl", "congestion_control", "congestion-controller");
                AddOptionalJsonString(proxy, root, extra, "udp-relay-mode", "UdpRelayMode", "udp_relay_mode", "udp-relay-mode");
                if (allowInsecure)
                {
                    proxy["skip-cert-verify"] = true;
                }
                break;

            case "anytls":
                var anyTlsPassword = GetJsonString(root, "Password", "password");
                if (string.IsNullOrWhiteSpace(anyTlsPassword))
                {
                    return false;
                }

                proxy["password"] = anyTlsPassword;
                ApplySni(proxy, sni);
                AddOptionalJsonString(proxy, root, "client-fingerprint", "Fingerprint", "fingerprint", "fp");
                AddAlpn(proxy, GetJsonString(root, extra, "Alpn", "ALPN", "alpn"));
                if (allowInsecure)
                {
                    proxy["skip-cert-verify"] = true;
                }
                break;

            default:
                return false;
        }

        node = new ProxyNode
        {
            Name = name,
            Type = type,
            Address = server,
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
               || content.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase)
               || content.Contains("proxy-providers:", StringComparison.OrdinalIgnoreCase)
               || content.Contains("rule-providers:", StringComparison.OrdinalIgnoreCase)
               || content.Contains("rules:", StringComparison.OrdinalIgnoreCase);
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

    private static bool TryParseAuthority(string authority, out string auth, out string server, out string portText)
    {
        auth = string.Empty;
        server = string.Empty;
        portText = "443";

        if (string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        var hostPort = authority;
        var atIndex = authority.LastIndexOf('@');
        if (atIndex >= 0)
        {
            auth = SafeUnescape(authority[..atIndex]);
            hostPort = authority[(atIndex + 1)..];
        }

        hostPort = hostPort.Trim();
        if (hostPort.StartsWith('['))
        {
            var endIndex = hostPort.IndexOf(']');
            if (endIndex <= 1)
            {
                return false;
            }

            server = hostPort[1..endIndex];
            var rest = hostPort[(endIndex + 1)..];
            if (rest.StartsWith(':'))
            {
                portText = SafeUnescape(rest[1..]);
            }

            return !string.IsNullOrWhiteSpace(server);
        }

        var colonIndex = hostPort.LastIndexOf(':');
        if (colonIndex > 0 && hostPort.IndexOf(':') == colonIndex)
        {
            server = hostPort[..colonIndex];
            portText = SafeUnescape(hostPort[(colonIndex + 1)..]);
        }
        else
        {
            server = hostPort;
        }

        server = SafeUnescape(server);
        return !string.IsNullOrWhiteSpace(server);
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
               && port > 0
               && port <= 65535;
    }

    private static int ExtractFirstPort(string value, int fallback)
    {
        var digits = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                digits.Append(character);
                continue;
            }

            if (digits.Length > 0)
            {
                break;
            }
        }

        return TryParsePort(digits.ToString(), out var port) ? port : fallback;
    }

    private static void AddOptionalString(
        Dictionary<string, object> proxy,
        Dictionary<string, string> query,
        string queryKey,
        string proxyKey)
    {
        if (query.TryGetValue(queryKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            proxy[proxyKey] = value;
        }
    }

    private static string GetFirstQueryValue(Dictionary<string, string> query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string DefaultIfBlank(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool IsValidPortNumber(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static void AddAlpn(Dictionary<string, object> proxy, string alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn))
        {
            return;
        }

        var values = alpn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (values.Count > 0)
        {
            proxy["alpn"] = values;
        }
    }

    private static void ApplySni(Dictionary<string, object> proxy, string sni)
    {
        if (!string.IsNullOrWhiteSpace(sni))
        {
            proxy["sni"] = sni;
        }
    }

    private static void ApplyTlsOptions(Dictionary<string, object> proxy, string security, string sni, bool allowInsecure)
    {
        var normalized = security.Trim().ToLowerInvariant();
        if (normalized is "tls" or "reality")
        {
            proxy["tls"] = true;
        }

        if (!string.IsNullOrWhiteSpace(sni))
        {
            proxy["sni"] = sni;
            proxy["servername"] = sni;
        }

        if (allowInsecure)
        {
            proxy["skip-cert-verify"] = true;
        }
    }

    private static void AddHysteriaSpeeds(Dictionary<string, object> proxy, JsonElement root, JsonElement extra)
    {
        AddOptionalJsonValue(proxy, root, extra, "up", "UpMbps", "Up", "upmbps", "up");
        AddOptionalJsonValue(proxy, root, extra, "down", "DownMbps", "Down", "downmbps", "down");
    }

    private static void AddHysteriaHopInterval(Dictionary<string, object> proxy, JsonElement root, JsonElement extra)
    {
        if (!TryGetJsonElement(root, out var value, "HopInterval", "hop-interval", "hopInterval")
            && (extra.ValueKind != JsonValueKind.Object
                || !TryGetJsonElement(extra, out value, "HopInterval", "hop-interval", "hopInterval")))
        {
            return;
        }

        var text = JsonValueToString(value);
        if (TryParseDurationSeconds(text, out var seconds))
        {
            proxy["hop-interval"] = seconds;
            return;
        }

        var converted = ConvertJsonValue(value);
        if (converted is not null && !string.IsNullOrWhiteSpace(converted.ToString()))
        {
            proxy["hop-interval"] = converted;
        }
    }

    private static bool TryParseDurationSeconds(string value, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            return seconds > 0;
        }

        var multiplier = 1d;
        var numberText = trimmed;
        if (trimmed.EndsWith("ms", StringComparison.Ordinal))
        {
            multiplier = 0.001d;
            numberText = trimmed[..^2];
        }
        else if (trimmed.EndsWith("s", StringComparison.Ordinal))
        {
            numberText = trimmed[..^1];
        }
        else if (trimmed.EndsWith("m", StringComparison.Ordinal))
        {
            multiplier = 60d;
            numberText = trimmed[..^1];
        }
        else if (trimmed.EndsWith("h", StringComparison.Ordinal))
        {
            multiplier = 3600d;
            numberText = trimmed[..^1];
        }

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return false;
        }

        seconds = Math.Max(1, (int)Math.Round(number * multiplier, MidpointRounding.AwayFromZero));
        return true;
    }

    private static void AddRealityOptions(Dictionary<string, object> proxy, JsonElement root, JsonElement extra)
    {
        var options = new Dictionary<string, object>();
        AddOptionalJsonString(options, root, extra, "public-key", "PublicKey", "public-key", "public_key", "pbk");
        AddOptionalJsonString(options, root, extra, "short-id", "ShortId", "short-id", "short_id", "sid");
        AddOptionalJsonString(options, root, extra, "spider-x", "SpiderX", "spider-x", "spider_x", "spx");

        if (options.Count > 0)
        {
            proxy["reality-opts"] = options;
        }
    }

    private static void AddJsonTransportOptions(Dictionary<string, object> proxy, JsonElement root, JsonElement extra)
    {
        var network = proxy.TryGetValue("network", out var networkValue)
            ? networkValue?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;

        if (network == "ws")
        {
            var wsOpts = new Dictionary<string, object>();
            AddOptionalJsonString(wsOpts, root, extra, "path", "Path", "path");
            var host = GetJsonString(root, extra, "Host", "RequestHost", "host");
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

            return;
        }

        if (network == "grpc")
        {
            var grpcOpts = new Dictionary<string, object>();
            AddOptionalJsonString(grpcOpts, root, extra, "grpc-service-name", "ServiceName", "serviceName", "service_name");
            if (grpcOpts.Count > 0)
            {
                proxy["grpc-opts"] = grpcOpts;
            }

            return;
        }

        if (network == "h2")
        {
            var h2Opts = new Dictionary<string, object>();
            AddOptionalJsonString(h2Opts, root, extra, "path", "Path", "path");
            var host = GetJsonString(root, extra, "Host", "RequestHost", "host");
            if (!string.IsNullOrWhiteSpace(host))
            {
                h2Opts["host"] = host
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
            }

            if (h2Opts.Count > 0)
            {
                proxy["h2-opts"] = h2Opts;
            }
        }
    }

    private static string NormalizeV2RayNType(string rawType, string configType)
    {
        var type = rawType.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type switch
            {
                "hy2" => "hysteria2",
                "hysteria2" => "hysteria2",
                "hysteria" => "hysteria",
                "shadowsocks" => "ss",
                "socks" => "socks5",
                _ => type
            };
        }

        return int.TryParse(configType, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value switch
            {
                1 => "vmess",
                2 => "vless",
                3 => "ss",
                5 => "vless",
                6 => "trojan",
                7 => "hysteria2",
                8 => "tuic",
                11 => "anytls",
                _ => string.Empty
            }
            : string.Empty;
    }

    private static string NormalizeNetwork(string network)
    {
        if (string.IsNullOrWhiteSpace(network))
        {
            return "tcp";
        }

        return network.Trim().ToLowerInvariant() switch
        {
            "raw" => "tcp",
            "http" => "h2",
            "http2" => "h2",
            "h2" => "h2",
            "ws" => "ws",
            "websocket" => "ws",
            "grpc" => "grpc",
            "xhttp" => "xhttp",
            "splithttp" => "xhttp",
            _ => network.Trim().ToLowerInvariant()
        };
    }

    private static bool TryGetJsonObject(JsonElement root, out JsonElement value, params string[] keys)
    {
        value = default;
        if (!TryGetJsonElement(root, out var element, keys) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        value = element;
        return true;
    }

    private static bool TryGetJsonElement(JsonElement root, out JsonElement value, params string[] keys)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out value))
            {
                return true;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetJsonString(JsonElement root, params string[] keys)
    {
        return TryGetJsonElement(root, out var value, keys) ? JsonValueToString(value) : string.Empty;
    }

    private static string GetJsonString(JsonElement root, JsonElement extra, params string[] keys)
    {
        var value = GetJsonString(root, keys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return extra.ValueKind == JsonValueKind.Object ? GetJsonString(extra, keys) : string.Empty;
    }

    private static int GetJsonInt(JsonElement root, params string[] keys)
    {
        if (!TryGetJsonElement(root, out var value, keys))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var intValue) ? intValue : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static bool GetJsonBool(JsonElement root, params string[] keys)
    {
        if (!TryGetJsonElement(root, out var value, keys))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var intValue) && intValue != 0,
            JsonValueKind.String => IsTrue(value.GetString() ?? string.Empty),
            _ => false
        };
    }

    private static void AddOptionalJsonString(
        Dictionary<string, object> proxy,
        JsonElement root,
        string targetKey,
        params string[] sourceKeys)
    {
        var value = GetJsonString(root, sourceKeys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            proxy[targetKey] = value;
        }
    }

    private static void AddOptionalJsonString(
        Dictionary<string, object> proxy,
        JsonElement root,
        JsonElement extra,
        string targetKey,
        params string[] sourceKeys)
    {
        var value = GetJsonString(root, extra, sourceKeys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            proxy[targetKey] = value;
        }
    }

    private static void AddOptionalJsonValue(
        Dictionary<string, object> proxy,
        JsonElement root,
        JsonElement extra,
        string targetKey,
        params string[] sourceKeys)
    {
        if (!TryGetJsonElement(root, out var value, sourceKeys)
            && (extra.ValueKind != JsonValueKind.Object || !TryGetJsonElement(extra, out value, sourceKeys)))
        {
            return;
        }

        var converted = ConvertJsonValue(value);
        if (converted is null || string.IsNullOrWhiteSpace(converted.ToString()))
        {
            return;
        }

        proxy[targetKey] = converted;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : value.TryGetDouble(out var doubleValue) ? doubleValue : string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => JsonValueToString(value)
        };
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static void AddTransportOptions(Dictionary<string, object> proxy, Dictionary<string, string> query)
    {
        var network = proxy.TryGetValue("network", out var networkValue)
            ? networkValue?.ToString() ?? string.Empty
            : string.Empty;

        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = new Dictionary<string, object>();
            if (query.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path))
            {
                wsOpts["path"] = path;
            }

            if (query.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host))
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

            return;
        }

        if (string.Equals(network, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            var grpcOpts = new Dictionary<string, object>();
            if (query.TryGetValue("serviceName", out var serviceName) && !string.IsNullOrWhiteSpace(serviceName))
            {
                grpcOpts["grpc-service-name"] = serviceName;
            }
            else if (query.TryGetValue("service_name", out var snakeServiceName) && !string.IsNullOrWhiteSpace(snakeServiceName))
            {
                grpcOpts["grpc-service-name"] = snakeServiceName;
            }

            if (grpcOpts.Count > 0)
            {
                proxy["grpc-opts"] = grpcOpts;
            }
        }
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    private static bool IsTrue(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
