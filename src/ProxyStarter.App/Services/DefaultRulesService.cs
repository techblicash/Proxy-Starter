using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace ProxyStarter.App.Services;

public sealed class DefaultRulesService
{
    private const string ReleaseTag = "20260208092911";
    private const string SourceUrl = "https://github.com/v2fly/domain-list-community/releases/download/20260208092911/dlc.dat_plain.yml";
    private const string TargetListName = "geolocation-cn";
    private const string CacheFileName = "default-rules-v2fly-20260208092911.txt";
    private const string SourceCacheFileName = "v2fly-dlc-20260208092911.yml";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cachePath;
    private readonly string _sourceYamlPath;
    private readonly object _sync = new();
    private IReadOnlyList<string>? _cachedRules;
    private bool _downloadAttempted;

    public DefaultRulesService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _cachePath = Path.Combine(AppPaths.DataDirectory, CacheFileName);
        _sourceYamlPath = Path.Combine(AppPaths.DataDirectory, SourceCacheFileName);
    }

    public IReadOnlyList<string> GetDefaultRules(string selectionGroup)
    {
        var targetGroup = string.IsNullOrWhiteSpace(selectionGroup)
            ? RoutingDefaults.FlatModeSelectionGroup
            : selectionGroup;
        var baseRules = EnsureBaseRulesLoaded();
        var rules = new List<string>(baseRules.Count + 1);
        rules.AddRange(baseRules);
        AppendMatchRule(rules, targetGroup);
        return rules;
    }

    private IReadOnlyList<string> EnsureBaseRulesLoaded()
    {
        if (_cachedRules is not null)
        {
            return _cachedRules;
        }

        lock (_sync)
        {
            if (_cachedRules is not null)
            {
                return _cachedRules;
            }

            var sourceRules = LoadFromSourceFile();
            if (sourceRules.Count > 0)
            {
                _cachedRules = sourceRules;
                SaveCache(sourceRules);
                return _cachedRules;
            }

            if (!_downloadAttempted)
            {
                _downloadAttempted = true;
                if (DownloadSourceFile())
                {
                    sourceRules = LoadFromSourceFile();
                    if (sourceRules.Count > 0)
                    {
                        _cachedRules = sourceRules;
                        SaveCache(sourceRules);
                        return _cachedRules;
                    }
                }
            }

            var cached = LoadFromCache();
            if (cached.Count > 0)
            {
                _cachedRules = cached;
                return _cachedRules;
            }

            _cachedRules = BuildFallbackRules();
            return _cachedRules;
        }
    }

    private List<string> LoadFromSourceFile()
    {
        try
        {
            foreach (var yaml in AtomicFile.ReadTextCandidates(_sourceYamlPath, Encoding.UTF8))
            {
                var rules = ParseGeolocationCnRules(yaml);
                if (rules.Count > 0)
                {
                    return rules;
                }
            }

            return new List<string>();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DefaultRulesService: LoadFromSourceFile");
            return new List<string>();
        }
    }

    private bool DownloadSourceFile()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var yaml = client.GetStringAsync(SourceUrl).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return false;
            }

            AtomicFile.WriteAllText(_sourceYamlPath, yaml, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DefaultRulesService: DownloadSourceFile");
            return false;
        }
    }

    private List<string> LoadFromCache()
    {
        try
        {
            foreach (var text in AtomicFile.ReadTextCandidates(_cachePath, Encoding.UTF8))
            {
                var lines = text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeCachedRule)
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                    .Where(line => !line.Equals("GEOIP,CN,DIRECT", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (lines.Count > 0)
                {
                    return lines;
                }
            }

            return new List<string>();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DefaultRulesService: LoadFromCache");
            return new List<string>();
        }
    }

    private void SaveCache(IReadOnlyList<string> rules)
    {
        try
        {
            var lines = new List<string>(rules.Count + 3)
            {
                $"# source: {SourceUrl}",
                $"# release-tag: {ReleaseTag}",
                $"# source-cache: {SourceCacheFileName}"
            };
            lines.AddRange(rules);

            AtomicFile.WriteAllLines(_cachePath, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DefaultRulesService: SaveCache");
        }
    }

    private static List<string> ParseGeolocationCnRules(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<string>();
        }

        var rules = new List<string>(7000);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(yaml);

        var inTargetList = false;
        var inRulesSection = false;

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- name: ", StringComparison.Ordinal))
            {
                var name = trimmed.Substring("- name: ".Length).Trim();
                if (inTargetList && !name.Equals(TargetListName, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                inTargetList = name.Equals(TargetListName, StringComparison.OrdinalIgnoreCase);
                inRulesSection = false;
                continue;
            }

            if (!inTargetList)
            {
                continue;
            }

            if (trimmed.StartsWith("rules:", StringComparison.Ordinal))
            {
                inRulesSection = true;
                continue;
            }

            if (!inRulesSection || !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var rawEntry = Unquote(trimmed.Substring(2).Trim());
            if (!TryConvertEntry(rawEntry, out var rule))
            {
                continue;
            }

            if (seen.Add(rule))
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private static string Unquote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }
        }

        return value;
    }

    private static bool TryConvertEntry(string rawEntry, out string rule)
    {
        rule = string.Empty;
        if (string.IsNullOrWhiteSpace(rawEntry))
        {
            return false;
        }

        var entry = StripAttributes(rawEntry).Trim();
        if (entry.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
        {
            var value = entry.Substring("domain:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            rule = $"DOMAIN-SUFFIX,{value},DIRECT";
            return true;
        }

        if (entry.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
        {
            var value = entry.Substring("full:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            rule = $"DOMAIN,{value},DIRECT";
            return true;
        }

        if (entry.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
        {
            var value = entry.Substring("regexp:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Contains(','))
            {
                return false;
            }

            rule = $"DOMAIN-REGEX,{value},DIRECT";
            return true;
        }

        return false;
    }

    private static string StripAttributes(string entry)
    {
        var marker = entry.IndexOf(" @", StringComparison.Ordinal);
        if (marker > 0)
        {
            return entry.Substring(0, marker);
        }

        marker = entry.LastIndexOf(":@", StringComparison.Ordinal);
        return marker > 0 ? entry.Substring(0, marker) : entry;
    }

    private static string NormalizeCachedRule(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
        {
            return trimmed;
        }

        var parts = trimmed.Split(',');
        if (parts.Length < 2)
        {
            return StripAttributes(trimmed).Trim();
        }

        if (parts[0].Equals("DOMAIN", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("DOMAIN-SUFFIX", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("DOMAIN-REGEX", StringComparison.OrdinalIgnoreCase))
        {
            parts[1] = StripAttributes(parts[1]).Trim();
            return string.Join(",", parts);
        }

        return trimmed;
    }

    private static List<string> BuildFallbackRules()
    {
        return new List<string>
        {
            "DOMAIN-SUFFIX,cn,DIRECT"
        };
    }

    private static void AppendMatchRule(ICollection<string> rules, string selectionGroup)
    {
        var hasMatch = rules.Any(rule => rule.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase));
        if (!hasMatch)
        {
            rules.Add($"MATCH,{selectionGroup}");
        }
    }
}
