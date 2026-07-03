using System;
using System.IO;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class ProxyCoreAdapterFactory
{
    private readonly MihomoCoreAdapter _mihomo;
    private readonly SingBoxCoreAdapter _singBox;

    public ProxyCoreAdapterFactory(MihomoCoreAdapter mihomo, SingBoxCoreAdapter singBox)
    {
        _mihomo = mihomo;
        _singBox = singBox;
    }

    public IProxyCoreAdapter Create(AppSettings settings)
    {
        var coreType = NormalizeCoreType(settings.CoreType);
        if (coreType == "sing-box" || LooksLikeSingBoxCore(settings.CorePath))
        {
            return _singBox;
        }

        return _mihomo;
    }

    public static string NormalizeCoreType(string? coreType)
    {
        var normalized = string.IsNullOrWhiteSpace(coreType)
            ? "mihomo"
            : coreType.Trim().ToLowerInvariant().Replace("_", "-");

        return normalized switch
        {
            "singbox" => "sing-box",
            "sing-box" => "sing-box",
            "clash-meta" => "mihomo",
            "clash.meta" => "mihomo",
            "mihomo" => "mihomo",
            _ => "mihomo"
        };
    }

    private static bool LooksLikeSingBoxCore(string corePath)
    {
        var coreFileName = Path.GetFileName(corePath);
        return coreFileName.Contains("sing-box", StringComparison.OrdinalIgnoreCase)
               || coreFileName.Contains("singbox", StringComparison.OrdinalIgnoreCase);
    }
}
