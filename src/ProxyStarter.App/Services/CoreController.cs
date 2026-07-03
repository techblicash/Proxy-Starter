using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Helpers;

namespace ProxyStarter.App.Services;

public sealed class CoreController : IDisposable
{
    private readonly IMihomoProcessService _processService;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCoreAdapterFactory _coreAdapterFactory;
    private readonly SystemProxyService _systemProxyService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CoreController(
        IMihomoProcessService processService,
        AppSettingsStore settingsStore,
        ProxyCoreAdapterFactory coreAdapterFactory,
        SystemProxyService systemProxyService)
    {
        _processService = processService;
        _settingsStore = settingsStore;
        _coreAdapterFactory = coreAdapterFactory;
        _systemProxyService = systemProxyService;

        _processService.RunningChanged += OnProcessRunningChanged;
    }

    public bool IsRunning => _processService.IsRunning;

    public event EventHandler<bool>? RunningChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var settings = _settingsStore.Settings;
            var adapter = _coreAdapterFactory.Create(settings);

            if (settings.TunEnabled && !ElevationHelper.IsRunningAsAdministrator())
            {
                CrashLogger.Log(
                    new InvalidOperationException("TUN mode requires administrator privileges."),
                    "CoreController: StartAsync");
                return;
            }

            var configPath = await Task.Run(() => adapter.EnsureConfig(settings), cancellationToken)
                .ConfigureAwait(false);
            var options = adapter.CreateLaunchOptions(settings, configPath);

            await _processService.StartAsync(options, cancellationToken).ConfigureAwait(false);

            if (_processService.IsRunning)
            {
                _systemProxyService.Apply(settings);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _processService.StopAsync(cancellationToken).ConfigureAwait(false);
            _systemProxyService.Restore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        return IsRunning ? StopAsync(cancellationToken) : StartAsync(cancellationToken);
    }

    private void OnProcessRunningChanged(object? sender, bool isRunning)
    {
        if (!isRunning)
        {
            _systemProxyService.Restore();
        }

        RunningChanged?.Invoke(this, isRunning);
    }

    public void Dispose()
    {
        _processService.RunningChanged -= OnProcessRunningChanged;
        _gate.Dispose();
    }
}
