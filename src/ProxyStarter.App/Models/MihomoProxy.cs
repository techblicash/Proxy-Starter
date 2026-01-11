using System.Collections.Generic;

namespace ProxyStarter.App.Models;

public sealed record MihomoProxy(
    string Name,
    string Type,
    string? Now,
    IReadOnlyList<string> All);
