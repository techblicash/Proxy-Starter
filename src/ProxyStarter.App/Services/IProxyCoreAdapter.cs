using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public interface IProxyCoreAdapter
{
    string CoreType { get; }
    string DisplayName { get; }
    bool SupportsConfigReload { get; }

    (string Path, string? NodeName) EnsureConfig(AppSettings settings, bool requireSelectedNode = false);
    MihomoLaunchOptions CreateLaunchOptions(AppSettings settings, string configPath);
}
