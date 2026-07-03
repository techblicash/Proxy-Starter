using System;
using System.IO;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class SingBoxCoreAdapter : IProxyCoreAdapter
{
    private readonly SingBoxConfigWriter _configWriter;

    public SingBoxCoreAdapter(SingBoxConfigWriter configWriter)
    {
        _configWriter = configWriter;
    }

    public string CoreType => "sing-box";
    public string DisplayName => "sing-box";
    public bool SupportsMihomoApi => false;

    public string EnsureConfig(AppSettings settings)
    {
        return _configWriter.EnsureConfig(settings);
    }

    public MihomoLaunchOptions CreateLaunchOptions(AppSettings settings, string configPath)
    {
        var corePath = ProxyCorePath.ResolveExecutable(settings.CorePath);
        var workingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory;

        return new MihomoLaunchOptions
        {
            CorePath = corePath,
            ConfigPath = configPath,
            WorkingDirectory = workingDirectory,
            Arguments = $"run -c \"{configPath}\""
        };
    }
}
