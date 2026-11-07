using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class CoreController : IDisposable
{
    private readonly IMihomoProcessService _processService;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCoreAdapterFactory _coreAdapterFactory;
    private readonly SystemProxyService _systemProxyService;
    private readonly MihomoApiClient _apiClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CoreController(
        IMihomoProcessService processService,
        AppSettingsStore settingsStore,
        ProxyCoreAdapterFactory coreAdapterFactory,
        SystemProxyService systemProxyService,
        MihomoApiClient apiClient)
    {
        _processService = processService;
        _settingsStore = settingsStore;
        _coreAdapterFactory = coreAdapterFactory;
        _systemProxyService = systemProxyService;
        _apiClient = apiClient;

        _processService.RunningChanged += OnProcessRunningChanged;
    }

    public bool IsRunning => _processService.IsRunning;
    public string? ActiveNodeName { get; private set; }

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

            var config = await Task.Run(() => adapter.EnsureConfig(settings), cancellationToken).ConfigureAwait(false);
            await StartPreparedAsync(adapter, settings, config, cancellationToken).ConfigureAwait(false);
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

    public Task SelectNodeAsync(ProxyNode node, CancellationToken cancellationToken = default) =>
        UpdateRoutingAsync(settings =>
        {
            settings.SelectedNodeSourceId = node.SourceId;
            settings.SelectedNodeName = RoutingDefaults.OriginalName(node);
        }, requireSelectedNode: true, cancellationToken: cancellationToken);

    public Task SetRoutingModeAsync(string mode) =>
        UpdateRoutingAsync(settings => settings.Mode = mode);

    public Task SetSubscriptionRulesAsync(string profileId) =>
        UpdateRoutingAsync(settings => settings.SubscriptionRulesProfileId = profileId);

    private async Task UpdateRoutingAsync(
        Action<AppSettings> update, bool requireSelectedNode = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settingsStore.Settings;
            var previous = (settings.SelectedNodeName, settings.SelectedNodeSourceId, settings.Mode, settings.SubscriptionRulesProfileId);
            var adapter = _coreAdapterFactory.Create(settings);
            var wasRunning = IsRunning;
            var wroteConfig = false;
            var attemptedReload = false;
            try
            {
                update(settings);
                if (!wasRunning && !requireSelectedNode)
                {
                    _settingsStore.Save();
                    return;
                }

                var config = await Task.Run(() => adapter.EnsureConfig(settings, requireSelectedNode), cancellationToken)
                    .ConfigureAwait(false);
                wroteConfig = true;
                _settingsStore.Save();
                if (wasRunning)
                {
                    attemptedReload = true;
                    await ApplyPreparedAsync(adapter, settings, config, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ActiveNodeName = config.NodeName;
                }
            }
            catch
            {
                (settings.SelectedNodeName, settings.SelectedNodeSourceId, settings.Mode, settings.SubscriptionRulesProfileId) = previous;
                try
                {
                    _settingsStore.Save();
                    if (wroteConfig)
                    {
                        var config = await Task.Run(() => adapter.EnsureConfig(settings)).ConfigureAwait(false);
                        if (attemptedReload)
                        {
                            await ApplyPreparedAsync(adapter, settings, config, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "CoreController: Restore routing configuration");
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyPreparedAsync(
        IProxyCoreAdapter adapter, AppSettings settings, (string Path, string? NodeName) config, CancellationToken cancellationToken)
    {
        if (IsRunning && adapter.SupportsConfigReload)
        {
            await _apiClient.ReloadConfigAsync(config.Path, cancellationToken).ConfigureAwait(false);
            ActiveNodeName = config.NodeName;
            return;
        }

        if (IsRunning)
        {
            await _processService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await StartPreparedAsync(adapter, settings, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartPreparedAsync(
        IProxyCoreAdapter adapter, AppSettings settings, (string Path, string? NodeName) config, CancellationToken cancellationToken)
    {
        var options = adapter.CreateLaunchOptions(settings, config.Path);
        await _processService.StartAsync(options, cancellationToken).ConfigureAwait(false);
        if (!IsRunning)
        {
            throw new InvalidOperationException("The proxy core did not start.");
        }

        try
        {
            await _apiClient.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            ActiveNodeName = config.NodeName;
            _systemProxyService.Apply(settings);
        }
        catch
        {
            await _processService.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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
