using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public static class RoutingDefaults
{
    public const string FlatModeSelectionGroup = "PROXY";
    public const string GroupSourceKey = "x-proxy-starter-source-id";

    public static string GroupName(Dictionary<string, object> group) =>
        group.TryGetValue("name", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    public static List<ProxyNode> GetFlatNodes(
        IReadOnlyList<ProxyNode> nodes, IReadOnlyList<Dictionary<string, object>> groups)
    {
        var groupedSources = groups.Select(GroupSource).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return nodes.Where(node => !groupedSources.Contains(node.SourceId)).ToList();
    }

    public static string GetFlatGroupName(
        IReadOnlyList<ProxyNode> nodes, IReadOnlyList<Dictionary<string, object>> groups)
    {
        var names = nodes.Select(node => node.Name).Concat(groups.Select(GroupName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = FlatModeSelectionGroup;
        for (var suffix = 2; names.Contains(name); suffix++)
        {
            name = $"{FlatModeSelectionGroup} {suffix}";
        }

        return name;
    }

    public static string GroupSource(Dictionary<string, object> group) =>
        group.TryGetValue(GroupSourceKey, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    internal static string GetDefaultGroupName(
        IReadOnlyList<Dictionary<string, object>> groups, bool hasFlatNodes, string flatGroup) =>
        groups.Count > 0 ? GroupName(groups[0]) : hasFlatNodes ? flatGroup : "DIRECT";

    public static IEnumerable<string> Names(Dictionary<string, object> group, string key)
    {
        if (!group.TryGetValue(key, out var value))
        {
            return Array.Empty<string>();
        }

        return value is IEnumerable<object> items
            ? items.Select(item => item?.ToString() ?? string.Empty).Where(name => !string.IsNullOrWhiteSpace(name))
            : value is string name && !string.IsNullOrWhiteSpace(name) ? new[] { name } : Array.Empty<string>();
    }

    public static bool IsMatch(string rule) =>
        rule.Split(',', 2)[0].Trim().Equals("MATCH", StringComparison.OrdinalIgnoreCase);

    internal static string RewriteRule(
        string rule, Func<string, string> mapTarget, Func<string, string>? mapRuleProvider = null)
    {
        var parts = SplitRule(rule);
        var targetIndex = IsMatch(rule) ? 1 : 2;
        if (parts.Count > targetIndex)
        {
            parts[targetIndex] = mapTarget(parts[targetIndex].Trim());
            if (mapRuleProvider is not null && parts[0].Trim().Equals("RULE-SET", StringComparison.OrdinalIgnoreCase))
            {
                parts[1] = mapRuleProvider(parts[1].Trim());
            }
        }

        return string.Join(",", parts);
    }

    internal static List<string> SplitRule(string rule)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < rule.Length; i++)
        {
            switch (rule[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' when depth == 0:
                    parts.Add(rule[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(rule[start..]);
        return parts;
    }

    internal static IEnumerable<string> ComposeRules(
        IReadOnlyList<string> customRules, IReadOnlyList<string> defaultRules,
        IReadOnlyList<string> subscriptionRules, string fallback)
    {
        return customRules.Where(rule => !IsMatch(rule))
            .Concat(defaultRules)
            .Concat(subscriptionRules.TakeWhile(rule => !IsMatch(rule)))
            .Append(customRules.FirstOrDefault(IsMatch) ?? subscriptionRules.FirstOrDefault(IsMatch) ?? $"MATCH,{fallback}");
    }
}
