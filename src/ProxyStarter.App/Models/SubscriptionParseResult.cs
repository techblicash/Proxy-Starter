using System;
using System.Collections.Generic;

namespace ProxyStarter.App.Models;

public sealed class SubscriptionParseResult
{
    public IReadOnlyList<Dictionary<string, object>> Proxies { get; init; } = new List<Dictionary<string, object>>();
    public IReadOnlyList<Dictionary<string, object>> ProxyGroups { get; init; } = new List<Dictionary<string, object>>();
    public IReadOnlyDictionary<string, object> RuleProviders { get; init; }
        = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Rules { get; init; } = new List<string>();
    public IReadOnlyList<ProxyNode> Nodes { get; init; } = new List<ProxyNode>();
}
