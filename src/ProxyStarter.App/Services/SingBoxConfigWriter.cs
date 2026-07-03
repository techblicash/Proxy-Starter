using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class SingBoxConfigWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] DefaultDirectDomainSuffixes =
    {
        "localhost",
        "local",
        "lan",
        "cn"
    };

    private static readonly string[] DefaultDirectIpCidrs =
    {
        "127.0.0.0/8",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "::1/128",
        "fc00::/7"
    };

    private static readonly string[] DefaultBlockDomainSuffixes =
    {
        "doubleclick.net",
        "googlesyndication.com"
    };

    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly RulesStore _rulesStore;
    private readonly DefaultRulesService _defaultRulesService;

    public SingBoxConfigWriter(
        ProxyCatalogStore proxyCatalogStore,
        RulesStore rulesStore,
        DefaultRulesService defaultRulesService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _rulesStore = rulesStore;
        _defaultRulesService = defaultRulesService;
    }

    public string EnsureConfig(AppSettings settings)
    {
        var configPath = ProxyCorePath.ResolveDataPath(settings.ConfigPath);
        var config = BuildConfig(settings);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        AtomicFile.WriteAllText(configPath, json, new UTF8Encoding(false));
        return configPath;
    }

    private Dictionary<string, object> BuildConfig(AppSettings settings)
    {
        var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "direct",
            "block"
        };
        var outbounds = new List<Dictionary<string, object>>();
        var proxyTags = new List<string>();

        foreach (var proxy in _proxyCatalogStore.LoadProxyDefinitions())
        {
            if (TryBuildOutbound(proxy, usedTags, out var outbound))
            {
                outbounds.Add(outbound);
                proxyTags.Add(GetString(outbound, "tag"));
            }
        }

        var selectionTag = proxyTags.Count > 0
            ? UniqueTag(RoutingDefaults.ResolveSelectionGroup(settings.SelectionGroup, settings.UseSubscriptionPolicyGroups), usedTags)
            : "direct";

        if (proxyTags.Count > 0)
        {
            var selectorOutbounds = new List<string>(proxyTags.Count + 2);
            selectorOutbounds.AddRange(proxyTags);
            selectorOutbounds.Add("direct");
            selectorOutbounds.Add("block");

            outbounds.Insert(0, new Dictionary<string, object>
            {
                ["type"] = "selector",
                ["tag"] = selectionTag,
                ["outbounds"] = selectorOutbounds,
                ["default"] = proxyTags[0]
            });
        }

        outbounds.Add(new Dictionary<string, object>
        {
            ["type"] = "direct",
            ["tag"] = "direct"
        });
        outbounds.Add(new Dictionary<string, object>
        {
            ["type"] = "block",
            ["tag"] = "block"
        });

        return new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object>
            {
                ["level"] = NormalizeSingBoxLogLevel(settings.LogLevel),
                ["timestamp"] = true
            },
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = outbounds,
            ["route"] = BuildRoute(settings, selectionTag)
        };
    }

    private static List<Dictionary<string, object>> BuildInbounds(AppSettings settings)
    {
        var listen = settings.AllowLan ? "0.0.0.0" : "127.0.0.1";
        var inbounds = new List<Dictionary<string, object>>();

        AddInbound(inbounds, "mixed", "mixed-in", listen, settings.MixedPort);
        AddInbound(inbounds, "http", "http-in", listen, settings.HttpPort);
        AddInbound(inbounds, "socks", "socks-in", listen, settings.SocksPort);

        if (inbounds.Count == 0)
        {
            AddInbound(inbounds, "mixed", "mixed-in", listen, 7890);
        }

        return inbounds;
    }

    private Dictionary<string, object> BuildRoute(AppSettings settings, string finalOutbound)
    {
        var mode = string.IsNullOrWhiteSpace(settings.Mode)
            ? "global"
            : settings.Mode.Trim().ToLowerInvariant();

        if (mode == "direct")
        {
            return new Dictionary<string, object>
            {
                ["final"] = "direct"
            };
        }

        if (mode == "global")
        {
            return new Dictionary<string, object>
            {
                ["final"] = finalOutbound
            };
        }

        var rules = new List<Dictionary<string, object>>();
        AddBlockedSiteRules(rules, settings.BlockedSites);
        AddDomainSuffixRule(rules, DefaultBlockDomainSuffixes, "block");
        AddIpCidrRule(rules, DefaultDirectIpCidrs, "direct");
        AddDomainSuffixRule(rules, DefaultDirectDomainSuffixes, "direct");

        foreach (var rule in LoadRoutingRules(settings, finalOutbound))
        {
            if (TryConvertClashRule(rule, finalOutbound, out var converted))
            {
                rules.Add(converted);
            }
        }

        return new Dictionary<string, object>
        {
            ["rules"] = rules,
            ["final"] = finalOutbound
        };
    }

    private IReadOnlyList<string> LoadRoutingRules(AppSettings settings, string finalOutbound)
    {
        var customRules = _rulesStore.LoadRules();
        if (customRules.Count > 0)
        {
            return customRules;
        }

        if (settings.UseSubscriptionPolicyGroups)
        {
            var subscriptionRules = _proxyCatalogStore.LoadRules();
            if (subscriptionRules.Count > 0)
            {
                return subscriptionRules;
            }
        }

        return _defaultRulesService.GetDefaultRules(finalOutbound);
    }

    private static bool TryBuildOutbound(
        Dictionary<string, object> proxy,
        HashSet<string> usedTags,
        out Dictionary<string, object> outbound)
    {
        outbound = new Dictionary<string, object>();
        var nodeType = NormalizeProxyType(GetString(proxy, "type"));
        if (nodeType is not "vmess"
            and not "vless"
            and not "trojan"
            and not "shadowsocks"
            and not "hysteria"
            and not "hysteria2"
            and not "tuic"
            and not "anytls"
            and not "http"
            and not "socks")
        {
            return false;
        }

        var server = GetString(proxy, "server");
        var port = GetInt(proxy, "port");
        if (string.IsNullOrWhiteSpace(server) || !IsValidPort(port))
        {
            return false;
        }

        var tag = UniqueTag(DefaultIfBlank(GetString(proxy, "name"), server), usedTags);
        outbound = new Dictionary<string, object>
        {
            ["type"] = nodeType,
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = port
        };

        switch (nodeType)
        {
            case "vmess":
                var vmessUuid = GetString(proxy, "uuid", "id");
                if (string.IsNullOrWhiteSpace(vmessUuid))
                {
                    return false;
                }

                outbound["uuid"] = vmessUuid;
                outbound["security"] = DefaultIfBlank(GetString(proxy, "cipher", "security"), "auto");
                outbound["alter_id"] = GetInt(proxy, "alterId", "alter-id", "alter_id");
                break;

            case "vless":
                var vlessUuid = GetString(proxy, "uuid", "id");
                if (string.IsNullOrWhiteSpace(vlessUuid))
                {
                    return false;
                }

                outbound["uuid"] = vlessUuid;
                AddIfNotBlank(outbound, "flow", GetString(proxy, "flow"));
                break;

            case "trojan":
                var trojanPassword = GetString(proxy, "password");
                if (string.IsNullOrWhiteSpace(trojanPassword))
                {
                    return false;
                }

                outbound["password"] = trojanPassword;
                break;

            case "shadowsocks":
                var method = GetString(proxy, "method", "cipher");
                var password = GetString(proxy, "password");
                if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(password))
                {
                    return false;
                }

                outbound["method"] = method;
                outbound["password"] = password;
                break;

            case "hysteria":
                var hysteriaAuth = GetString(proxy, "auth-str", "auth_str", "auth");
                if (string.IsNullOrWhiteSpace(hysteriaAuth))
                {
                    return false;
                }

                outbound["auth_str"] = hysteriaAuth;
                AddPositiveInt(outbound, "up_mbps", proxy, "up", "upmbps", "up_mbps");
                AddPositiveInt(outbound, "down_mbps", proxy, "down", "downmbps", "down_mbps");
                AddIfNotBlank(outbound, "obfs", GetString(proxy, "obfs"));
                break;

            case "hysteria2":
                var hysteria2Password = GetString(proxy, "password");
                if (string.IsNullOrWhiteSpace(hysteria2Password))
                {
                    return false;
                }

                outbound["password"] = hysteria2Password;
                AddPositiveInt(outbound, "up_mbps", proxy, "up", "upmbps", "up_mbps");
                AddPositiveInt(outbound, "down_mbps", proxy, "down", "downmbps", "down_mbps");

                var obfsPassword = GetString(proxy, "obfs-password", "obfs_password");
                if (!string.IsNullOrWhiteSpace(obfsPassword))
                {
                    outbound["obfs"] = new Dictionary<string, object>
                    {
                        ["type"] = DefaultIfBlank(GetString(proxy, "obfs"), "salamander"),
                        ["password"] = obfsPassword
                    };
                }
                break;

            case "tuic":
                var tuicUuid = GetString(proxy, "uuid", "username");
                var tuicPassword = GetString(proxy, "password");
                if (string.IsNullOrWhiteSpace(tuicUuid) || string.IsNullOrWhiteSpace(tuicPassword))
                {
                    return false;
                }

                outbound["uuid"] = tuicUuid;
                outbound["password"] = tuicPassword;
                AddIfNotBlank(outbound, "congestion_control", GetString(proxy, "congestion-controller", "congestion_control"));
                AddIfNotBlank(outbound, "udp_relay_mode", GetString(proxy, "udp-relay-mode", "udp_relay_mode"));
                if (GetBool(proxy, "reduce-rtt", "reduce_rtt", "zero_rtt_handshake"))
                {
                    outbound["zero_rtt_handshake"] = true;
                }
                break;

            case "anytls":
                var anyTlsPassword = GetString(proxy, "password");
                if (string.IsNullOrWhiteSpace(anyTlsPassword))
                {
                    return false;
                }

                outbound["password"] = anyTlsPassword;
                break;

            case "http":
            case "socks":
                AddIfNotBlank(outbound, "username", GetString(proxy, "username", "user"));
                AddIfNotBlank(outbound, "password", GetString(proxy, "password", "pass"));
                break;
        }

        var tls = BuildTls(proxy, nodeType);
        if (tls is not null)
        {
            outbound["tls"] = tls;
        }

        var transport = BuildTransport(proxy);
        if (transport is not null)
        {
            outbound["transport"] = transport;
        }

        return true;
    }

    private static Dictionary<string, object>? BuildTls(Dictionary<string, object> proxy, string nodeType)
    {
        var security = GetString(proxy, "security").Trim().ToLowerInvariant();
        var sni = GetString(proxy, "sni", "servername", "server_name", "peer");
        var tlsEnabled = GetBool(proxy, "tls")
                         || nodeType == "trojan"
                         || nodeType is "hysteria" or "hysteria2" or "tuic" or "anytls"
                         || security is "tls" or "reality"
                         || !string.IsNullOrWhiteSpace(sni)
                         || GetBool(proxy, "skip-cert-verify", "allowInsecure");

        if (!tlsEnabled)
        {
            return null;
        }

        var tls = new Dictionary<string, object>
        {
            ["enabled"] = true
        };
        AddIfNotBlank(tls, "server_name", sni);

        if (GetBool(proxy, "skip-cert-verify", "allowInsecure"))
        {
            tls["insecure"] = true;
        }

        var alpn = GetStringList(proxy, "alpn");
        if (alpn.Count > 0)
        {
            tls["alpn"] = alpn;
        }

        var fingerprint = GetString(proxy, "client-fingerprint", "client_fingerprint", "fp");
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            tls["utls"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["fingerprint"] = fingerprint
            };
        }

        if (security == "reality")
        {
            var reality = new Dictionary<string, object>
            {
                ["enabled"] = true
            };

            var realityOptions = GetMap(proxy, "reality-opts", "reality_opts", "reality");
            AddIfNotBlank(reality, "public_key", GetString(realityOptions, "public-key", "public_key", "pbk"));
            AddIfNotBlank(reality, "short_id", GetString(realityOptions, "short-id", "short_id", "sid"));
            AddIfNotBlank(reality, "public_key", GetString(proxy, "public-key", "public_key", "pbk"));
            AddIfNotBlank(reality, "short_id", GetString(proxy, "short-id", "short_id", "sid"));

            tls["reality"] = reality;
        }

        return tls;
    }

    private static Dictionary<string, object>? BuildTransport(Dictionary<string, object> proxy)
    {
        var network = DefaultIfBlank(GetString(proxy, "network", "net"), "tcp").Trim().ToLowerInvariant();
        if (network is "" or "tcp")
        {
            return null;
        }

        if (network == "ws")
        {
            var wsOptions = GetMap(proxy, "ws-opts", "ws_opts");
            var headers = GetMap(wsOptions, "headers");
            var transport = new Dictionary<string, object>
            {
                ["type"] = "ws"
            };

            AddIfNotBlank(transport, "path", GetString(wsOptions, "path"));
            AddIfNotBlank(transport, "path", GetString(proxy, "path"));

            var host = GetString(headers, "Host", "host");
            host = DefaultIfBlank(host, GetString(proxy, "host"));
            if (!string.IsNullOrWhiteSpace(host))
            {
                transport["headers"] = new Dictionary<string, object>
                {
                    ["Host"] = host
                };
            }

            return transport;
        }

        if (network == "grpc")
        {
            var grpcOptions = GetMap(proxy, "grpc-opts", "grpc_opts");
            var transport = new Dictionary<string, object>
            {
                ["type"] = "grpc"
            };
            AddIfNotBlank(transport, "service_name", GetString(grpcOptions, "grpc-service-name", "serviceName", "service_name"));
            return transport;
        }

        return new Dictionary<string, object>
        {
            ["type"] = network
        };
    }

    private static bool TryConvertClashRule(string rule, string finalOutbound, out Dictionary<string, object> converted)
    {
        converted = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        var parts = rule.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var kind = parts[0].Trim().ToUpperInvariant();
        if (kind == "MATCH")
        {
            return false;
        }

        var policyIndex = kind == "GEOIP" || kind == "GEOSITE" ? 2 : 2;
        if (policyIndex >= parts.Length)
        {
            return false;
        }

        var value = parts[1].Trim();
        var outbound = MapPolicy(parts[policyIndex], finalOutbound);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        converted["outbound"] = outbound;
        switch (kind)
        {
            case "DOMAIN":
                converted["domain"] = new[] { value };
                return true;
            case "DOMAIN-SUFFIX":
                converted["domain_suffix"] = new[] { value };
                return true;
            case "DOMAIN-KEYWORD":
                converted["domain_keyword"] = new[] { value };
                return true;
            case "DOMAIN-REGEX":
                converted["domain_regex"] = new[] { value };
                return true;
            case "IP-CIDR":
            case "IP-CIDR6":
                converted["ip_cidr"] = new[] { value };
                return true;
            case "GEOIP":
                converted["geoip"] = new[] { value.ToLowerInvariant() };
                return true;
            case "GEOSITE":
                converted["geosite"] = new[] { value.ToLowerInvariant() };
                return true;
            default:
                return false;
        }
    }

    private static void AddBlockedSiteRules(List<Dictionary<string, object>> rules, string? blockedSites)
    {
        if (string.IsNullOrWhiteSpace(blockedSites))
        {
            return;
        }

        foreach (var rawLine in blockedSites.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.Contains(',') && TryConvertClashRule(line, "block", out var converted))
            {
                converted["outbound"] = "block";
                rules.Add(converted);
                continue;
            }

            var host = ExtractHost(line);
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            if (TryFormatIpCidr(host, out var cidr))
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["ip_cidr"] = new[] { cidr },
                    ["outbound"] = "block"
                });
            }
            else
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["domain_suffix"] = new[] { host },
                    ["outbound"] = "block"
                });
            }
        }
    }

    private static void AddDomainSuffixRule(List<Dictionary<string, object>> rules, IReadOnlyCollection<string> domains, string outbound)
    {
        if (domains.Count == 0)
        {
            return;
        }

        rules.Add(new Dictionary<string, object>
        {
            ["domain_suffix"] = domains,
            ["outbound"] = outbound
        });
    }

    private static void AddIpCidrRule(List<Dictionary<string, object>> rules, IReadOnlyCollection<string> cidrs, string outbound)
    {
        if (cidrs.Count == 0)
        {
            return;
        }

        rules.Add(new Dictionary<string, object>
        {
            ["ip_cidr"] = cidrs,
            ["outbound"] = outbound
        });
    }

    private static void AddInbound(List<Dictionary<string, object>> inbounds, string type, string tag, string listen, int port)
    {
        if (!IsValidPort(port))
        {
            return;
        }

        inbounds.Add(new Dictionary<string, object>
        {
            ["type"] = type,
            ["tag"] = tag,
            ["listen"] = listen,
            ["listen_port"] = port
        });
    }

    private static string NormalizeProxyType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "ss" => "shadowsocks",
            "shadowsocks" => "shadowsocks",
            "vmess" => "vmess",
            "vless" => "vless",
            "trojan" => "trojan",
            "hysteria" => "hysteria",
            "hysteria2" => "hysteria2",
            "tuic" => "tuic",
            "anytls" => "anytls",
            "http" => "http",
            "https" => "http",
            "socks" => "socks",
            "socks5" => "socks",
            _ => string.Empty
        };
    }

    private static string NormalizeSingBoxLogLevel(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "info"
            : value.Trim().ToLowerInvariant() switch
            {
                "warning" => "warn",
                "silent" => "panic",
                "debug" => "debug",
                "error" => "error",
                _ => "info"
            };
    }

    private static string MapPolicy(string policy, string finalOutbound)
    {
        if (policy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
        {
            return "direct";
        }

        if (policy.Equals("REJECT", StringComparison.OrdinalIgnoreCase)
            || policy.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase))
        {
            return "block";
        }

        return finalOutbound;
    }

    private static string UniqueTag(string name, HashSet<string> usedTags)
    {
        var baseName = DefaultIfBlank(name, "proxy");
        if (usedTags.Add(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseName} ({suffix})";
            if (usedTags.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static IReadOnlyDictionary<string, object> GetMap(Dictionary<string, object> map, params string[] keys)
    {
        return GetMap((IReadOnlyDictionary<string, object>)map, keys);
    }

    private static IReadOnlyDictionary<string, object> GetMap(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        var value = GetValue(map, keys);
        return value switch
        {
            IReadOnlyDictionary<string, object> stringMap => stringMap,
            IDictionary<string, object> stringMap => new Dictionary<string, object>(stringMap, StringComparer.OrdinalIgnoreCase),
            IDictionary<object, object> objectMap => objectMap
                .Where(pair => pair.Key is not null)
                .ToDictionary(pair => pair.Key.ToString() ?? string.Empty, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string GetString(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        var value = GetValue(map, keys);
        return value?.ToString() ?? string.Empty;
    }

    private static int GetInt(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        var value = GetValue(map, keys);
        return value switch
        {
            int intValue => intValue,
            long longValue => longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : 0,
            double doubleValue => (int)doubleValue,
            _ => int.TryParse(value?.ToString(), out var parsed) ? parsed : 0
        };
    }

    private static IReadOnlyList<string> GetStringList(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        var value = GetValue(map, keys);
        switch (value)
        {
            case IEnumerable<string> strings:
                return strings
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .ToList();
            case IEnumerable<object> objects:
                return objects
                    .Select(item => item?.ToString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .ToList();
            default:
                var text = value?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return Array.Empty<string>();
                }

                return text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
        }
    }

    private static bool GetBool(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        var value = GetValue(map, keys);
        return value switch
        {
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => IsTrue(value?.ToString())
        };
    }

    private static object? GetValue(IReadOnlyDictionary<string, object> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var pair in map)
            {
                if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static void AddIfNotBlank(Dictionary<string, object> target, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static void AddPositiveInt(
        Dictionary<string, object> target,
        string key,
        IReadOnlyDictionary<string, object> source,
        params string[] sourceKeys)
    {
        var value = GetInt(source, sourceKeys);
        if (value > 0)
        {
            target[key] = value;
        }
    }

    private static string DefaultIfBlank(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool IsValidPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static bool IsTrue(string? value)
    {
        return value is not null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
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

    private static bool TryFormatIpCidr(string value, out string cidr)
    {
        cidr = string.Empty;
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
                cidr = $"{parts[0]}/{prefix}";
                return true;
            }
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            cidr = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{trimmed}/32"
                : $"{trimmed}/128";
            return true;
        }

        return false;
    }
}
