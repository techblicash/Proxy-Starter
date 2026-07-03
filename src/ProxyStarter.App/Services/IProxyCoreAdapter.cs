using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public interface IProxyCoreAdapter
{
    string CoreType { get; }
    string DisplayName { get; }
    bool SupportsMihomoApi { get; }

    string EnsureConfig(AppSettings settings);
    MihomoLaunchOptions CreateLaunchOptions(AppSettings settings, string configPath);
}
