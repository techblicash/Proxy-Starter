using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyStarter.App.Services;

public static class CoreCatalog
{
    private static readonly IReadOnlyList<CoreDefinition> Definitions =
    [
        new CoreDefinition(
            "mihomo",
            "Mihomo",
            "MetaCubeX/mihomo",
            @"runtime\mihomo\mihomo.exe",
            "config.yaml",
            "mihomo.exe"),
        new CoreDefinition(
            "sing-box",
            "sing-box",
            "SagerNet/sing-box",
            @"runtime\sing-box\sing-box.exe",
            "sing-box.json",
            "sing-box.exe")
    ];

    public static IReadOnlyList<CoreDefinition> All => Definitions;

    public static CoreDefinition Get(string coreType)
    {
        var normalized = ProxyCoreAdapterFactory.NormalizeCoreType(coreType);
        return Definitions.FirstOrDefault(definition => definition.CoreType.Equals(normalized, StringComparison.OrdinalIgnoreCase))
               ?? Definitions[0];
    }

    public static bool IsDefaultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return Definitions.Any(definition =>
            definition.RelativeExecutablePath.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase)
            || LegacyDefaultPathFor(definition.CoreType).Equals(path.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string LegacyDefaultPathFor(string coreType)
    {
        return coreType.Equals("mihomo", StringComparison.OrdinalIgnoreCase)
            ? @"runtime\mihomo.exe"
            : string.Empty;
    }
}
