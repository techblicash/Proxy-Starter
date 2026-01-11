using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class CoreController
{
    private readonly IMihomoProcessService _processService;
    private readonly AppSettingsStore _settingsStore;
    private readonly ConfigWriter _configWriter;

    public CoreController(
        IMihomoProcessService processService,
        AppSettingsStore settingsStore,
        ConfigWriter configWriter)
    {
        _processService = processService;
        _settingsStore = settingsStore;
        _configWriter = configWriter;

        _processService.RunningChanged += (_, isRunning) => RunningChanged?.Invoke(this, isRunning);
    }

    public bool IsRunning => _processService.IsRunning;

    public event EventHandler<bool>? RunningChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Settings;
        var configPath = _configWriter.EnsureConfig(settings);
        var corePath = ResolveCorePath(settings.CorePath);

        var options = new MihomoLaunchOptions
        {
            CorePath = corePath,
            ConfigPath = configPath,
            WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory
        };

        return _processService.StartAsync(options, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _processService.StopAsync(cancellationToken);
    }

    public Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        return IsRunning ? StopAsync(cancellationToken) : StartAsync(cancellationToken);
    }

    private static string ResolveCorePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }
}
