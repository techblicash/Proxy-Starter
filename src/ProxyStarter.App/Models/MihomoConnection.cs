using System;
using System.Collections.Generic;

namespace ProxyStarter.App.Models;

public sealed record MihomoConnection(
    string Id,
    string Target,
    string Network,
    string Type,
    string Rule,
    string Proxy,
    string Process,
    long Upload,
    long Download,
    DateTimeOffset Start,
    IReadOnlyList<string> Chains);
