using System.Collections.Generic;

namespace ProxyStarter.App.Models;

public sealed record MihomoProxiesSnapshot(IReadOnlyDictionary<string, MihomoProxy> Proxies);
