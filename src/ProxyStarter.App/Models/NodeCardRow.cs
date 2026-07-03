using System.Collections.Generic;

namespace ProxyStarter.App.Models;

public sealed record NodeCardRow(IReadOnlyList<ProxyNode> Items, string Header = "", int Count = 0)
{
    public bool IsHeader => !string.IsNullOrWhiteSpace(Header);

    public static NodeCardRow Cards(IReadOnlyList<ProxyNode> items)
    {
        return new NodeCardRow(items);
    }

    public static NodeCardRow Section(string header, int count)
    {
        return new NodeCardRow(System.Array.Empty<ProxyNode>(), header, count);
    }
}
