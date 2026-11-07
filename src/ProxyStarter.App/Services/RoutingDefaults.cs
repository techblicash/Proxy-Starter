using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public static class RoutingDefaults
{
    internal static string OutboundName(Dictionary<string, object> proxy) =>
        proxy.TryGetValue("name", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    internal static string OriginalName(ProxyNode node) =>
        string.IsNullOrEmpty(node.OriginalName) ? node.Name : node.OriginalName;

    internal static ProxyNode? FindSelectedNode(AppSettings settings, IEnumerable<ProxyNode> nodes) =>
        nodes.FirstOrDefault(node => string.Equals(node.SourceId, settings.SelectedNodeSourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(OriginalName(node), settings.SelectedNodeName, StringComparison.Ordinal));

    internal static string? ResolveSelectedNode(
        AppSettings settings, IReadOnlyList<ProxyNode> nodes, HashSet<string> outboundNames, bool requireSelectedNode)
    {
        var available = nodes.Where(node => outboundNames.Contains(node.Name)).ToList();
        var selected = FindSelectedNode(settings, available);
        if (requireSelectedNode && selected is null)
        {
            throw new InvalidOperationException($"The selected node is unavailable for this core: {settings.SelectedNodeName}");
        }

        return (selected ?? available.FirstOrDefault())?.Name;
    }

    internal static string MapMihomoTarget(string target, string selectedNode) =>
        target.ToUpperInvariant() switch
        {
            "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "DNS" => target.ToUpperInvariant(),
            _ => selectedNode
        };

    public static bool IsMatch(string rule) =>
        rule.Split(',', 2)[0].Trim().Equals("MATCH", StringComparison.OrdinalIgnoreCase);

    internal static string RewriteRule(
        string rule, Func<string, string> mapTarget)
    {
        var parts = SplitRule(rule);
        var targetIndex = IsMatch(rule) ? 1 : 2;
        if (parts.Count > targetIndex)
        {
            parts[targetIndex] = mapTarget(parts[targetIndex].Trim());
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
            .Concat(subscriptionRules.TakeWhile(rule => !IsMatch(rule)))
            .Concat(defaultRules)
            .Append(customRules.FirstOrDefault(IsMatch) ?? subscriptionRules.FirstOrDefault(IsMatch) ?? $"MATCH,{fallback}");
    }
}
