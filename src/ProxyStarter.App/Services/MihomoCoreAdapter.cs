using System.IO;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoCoreAdapter : IProxyCoreAdapter
{
    private readonly ConfigWriter _configWriter;

    public MihomoCoreAdapter(ConfigWriter configWriter)
    {
        _configWriter = configWriter;
    }

    public string CoreType => "mihomo";
    public string DisplayName => "Mihomo";
    public bool SupportsConfigReload => true;

    public (string Path, string? NodeName) EnsureConfig(AppSettings settings, bool requireSelectedNode = false)
    {
        return _configWriter.EnsureConfig(settings, requireSelectedNode);
    }

    public MihomoLaunchOptions CreateLaunchOptions(AppSettings settings, string configPath)
    {
        var corePath = ProxyCorePath.ResolveExecutable(settings.CorePath);
        var homeDirectory = Path.GetDirectoryName(configPath) ?? AppPaths.DataDirectory;

        return new MihomoLaunchOptions
        {
            CorePath = corePath,
            ConfigPath = configPath,
            WorkingDirectory = homeDirectory,
            ExtraArguments = $"-d \"{homeDirectory}\""
        };
    }
}
