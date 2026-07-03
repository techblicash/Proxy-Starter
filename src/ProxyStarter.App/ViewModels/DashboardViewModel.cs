using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class DashboardViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly CoreController _coreController;
    private readonly TrafficMonitorService _trafficMonitorService;
    private readonly ConnectionMonitorService _connectionMonitorService;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly SubscriptionStore _subscriptionStore;
    private readonly IDialogService _dialogService;
    private readonly LocalizationService _localizationService;
    private bool _isActive;
    private TrafficSnapshot _latestTraffic = new(0, 0, 0, 0);
    private ConnectionStatusSnapshot _latestStatus = new(0, 0, string.Empty, null);
    private int _trafficUiQueued;
    private int _statusUiQueued;

    [ObservableProperty]
    private string _statusText = "Stopped";

    [ObservableProperty]
    private string _activeProfile = "None";

    [ObservableProperty]
    private string _activeNode = "Auto";

    [ObservableProperty]
    private string _mode = "Rule";

    [ObservableProperty]
    private string _currentAddress = "Unknown";

    [ObservableProperty]
    private string _uploadSpeed = "0 KB/s";

    [ObservableProperty]
    private string _downloadSpeed = "0 KB/s";

    [ObservableProperty]
    private string _uploadTotal = "0 B";

    [ObservableProperty]
    private string _downloadTotal = "0 B";

    [ObservableProperty]
    private string _coreToggleLabel = "Start Core";

    [ObservableProperty]
    private int _mixedPort;

    [ObservableProperty]
    private int _httpPort;

    [ObservableProperty]
    private int _socksPort;

    [ObservableProperty]
    private int _apiPort;

    [ObservableProperty]
    private bool _tunEnabled;

    public DashboardViewModel(
        CoreController coreController,
        TrafficMonitorService trafficMonitorService,
        ConnectionMonitorService connectionMonitorService,
        AppSettingsStore settingsStore,
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionStore subscriptionStore,
        IDialogService dialogService,
        LocalizationService localizationService)
    {
        _coreController = coreController;
        _trafficMonitorService = trafficMonitorService;
        _connectionMonitorService = connectionMonitorService;
        _settingsStore = settingsStore;
        _proxyCatalogStore = proxyCatalogStore;
        _subscriptionStore = subscriptionStore;
        _dialogService = dialogService;
        _localizationService = localizationService;

        Mode = _settingsStore.Settings.Mode;
        LoadNetworkSettings();
        UpdateStatus();
        _coreController.RunningChanged += (_, _) => Dispatch(UpdateStatus);
        _localizationService.LanguageChanged += (_, _) => Dispatch(UpdateStatus);

        // Monitors are started only while the Dashboard page is visible to reduce UI churn.
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (!_coreController.IsRunning)
        {
            if (_settingsStore.Settings.TunEnabled && !ElevationHelper.IsRunningAsAdministrator())
            {
                var relaunched = await RequestElevationForTunAsync();
                if (relaunched)
                {
                    Application.Current?.Shutdown();
                }

                return;
            }

            var corePath = ProxyCorePath.ResolveExecutable(_settingsStore.Settings.CorePath);
            if (!File.Exists(corePath))
            {
                await _dialogService.ShowErrorAsync(
                    "Core Not Found",
                    $"Core executable was not found:\n{corePath}\n\nPlease update Core Path in Settings.");
                return;
            }
        }

        try
        {
            await _coreController.ToggleAsync();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DashboardViewModel: Toggle core");
            await _dialogService.ShowErrorAsync("Core Operation Failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveNetworkAsync()
    {
        try
        {
            var settings = _settingsStore.Settings;

            if (TunEnabled && !ElevationHelper.IsRunningAsAdministrator())
            {
                var relaunched = await RequestElevationForTunAsync();
                if (relaunched)
                {
                    Application.Current?.Shutdown();
                }
                else
                {
                    TunEnabled = settings.TunEnabled;
                }

                return;
            }

            var wasRunning = _coreController.IsRunning;
            var needsRestart =
                settings.MixedPort != MixedPort
                || settings.HttpPort != HttpPort
                || settings.SocksPort != SocksPort
                || settings.ApiPort != ApiPort
                || settings.TunEnabled != TunEnabled;

            settings.MixedPort = MixedPort;
            settings.HttpPort = HttpPort;
            settings.SocksPort = SocksPort;
            settings.ApiPort = ApiPort;
            settings.TunEnabled = TunEnabled;
            _settingsStore.Save();

            if (wasRunning && needsRestart)
            {
                await _coreController.StopAsync();
                await _coreController.StartAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "DashboardViewModel: Save network");
            await _dialogService.ShowErrorAsync("Network Settings Failed", ex.Message);
            LoadNetworkSettings();
        }
    }

    [RelayCommand]
    private void ReloadNetwork()
    {
        LoadNetworkSettings();
    }

    private void UpdateStatus()
    {
        StatusText = _coreController.IsRunning
            ? _localizationService.GetString("Text_Running", "Running")
            : _localizationService.GetString("Text_Stopped", "Stopped");
        CoreToggleLabel = _coreController.IsRunning
            ? _localizationService.GetString("Text_StopCore", "Stop Core")
            : _localizationService.GetString("Text_StartCore", "Start Core");
    }

    private void OnTrafficUpdated(object? sender, TrafficSnapshot snapshot)
    {
        _latestTraffic = snapshot;
        if (_isActive)
        {
            QueueTrafficUiUpdate();
        }
    }

    private void OnStatusUpdated(object? sender, ConnectionStatusSnapshot snapshot)
    {
        _latestStatus = snapshot;
        if (_isActive)
        {
            QueueStatusUiUpdate();
        }
    }

    private void LoadNetworkSettings()
    {
        var settings = _settingsStore.Settings;
        MixedPort = settings.MixedPort;
        HttpPort = settings.HttpPort;
        SocksPort = settings.SocksPort;
        ApiPort = settings.ApiPort;
        TunEnabled = settings.TunEnabled;
    }

    private static void Dispatch(Action action)
    {
        if (Application.Current is null)
        {
            action();
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void OnPageActivated()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;

        try
        {
            _trafficMonitorService.TrafficUpdated -= OnTrafficUpdated;
            _trafficMonitorService.TrafficUpdated += OnTrafficUpdated;
            _trafficMonitorService.Start();
        }
        catch
        {
        }

        try
        {
            _connectionMonitorService.StatusUpdated -= OnStatusUpdated;
            _connectionMonitorService.StatusUpdated += OnStatusUpdated;
            _connectionMonitorService.Start();
        }
        catch
        {
        }

        QueueTrafficUiUpdate();
        QueueStatusUiUpdate();
    }

    public void OnPageDeactivated()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;

        try
        {
            _trafficMonitorService.TrafficUpdated -= OnTrafficUpdated;
            _trafficMonitorService.Stop();
        }
        catch
        {
        }

        try
        {
            _connectionMonitorService.StatusUpdated -= OnStatusUpdated;
            _connectionMonitorService.Stop();
        }
        catch
        {
        }
    }

    private void QueueTrafficUiUpdate()
    {
        if (Interlocked.Exchange(ref _trafficUiQueued, 1) == 1)
        {
            return;
        }

        Dispatch(() =>
        {
            try
            {
                var snapshot = _latestTraffic;
                UploadSpeed = FormatSpeed(snapshot.UploadBytesPerSecond);
                DownloadSpeed = FormatSpeed(snapshot.DownloadBytesPerSecond);
            }
            finally
            {
                Interlocked.Exchange(ref _trafficUiQueued, 0);
            }
        });
    }

    private void QueueStatusUiUpdate()
    {
        if (Interlocked.Exchange(ref _statusUiQueued, 1) == 1)
        {
            return;
        }

        Dispatch(() =>
        {
            try
            {
                var snapshot = _latestStatus;
                ActiveNode = snapshot.ActiveNode;
                ActiveProfile = ResolveActiveProfile(snapshot.ActiveNode);
                CurrentAddress = string.IsNullOrWhiteSpace(snapshot.CurrentAddress) ? "Unknown" : snapshot.CurrentAddress;
                UploadTotal = FormatBytes(snapshot.UploadTotal);
                DownloadTotal = FormatBytes(snapshot.DownloadTotal);
            }
            finally
            {
                Interlocked.Exchange(ref _statusUiQueued, 0);
            }
        });
    }

    private string ResolveActiveProfile(string activeNode)
    {
        if (string.IsNullOrWhiteSpace(activeNode)
            || activeNode.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
            || activeNode.Equals("REJECT", StringComparison.OrdinalIgnoreCase)
            || activeNode.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)
            || activeNode.Equals(RoutingDefaults.FlatModeSelectionGroup, StringComparison.OrdinalIgnoreCase))
        {
            return "None";
        }

        try
        {
            var node = _proxyCatalogStore.LoadNodes()
                .FirstOrDefault(item => item.Name.Equals(activeNode, StringComparison.OrdinalIgnoreCase));
            if (node is null || string.IsNullOrWhiteSpace(node.SourceId))
            {
                return "None";
            }

            var profile = _subscriptionStore.Load()
                .FirstOrDefault(item => item.Id.Equals(node.SourceId, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(profile?.Name) ? "None" : profile.Name;
        }
        catch
        {
            return "None";
        }
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        return FormatBytes(bytesPerSecond) + "/s";
    }

    private async Task<bool> RequestElevationForTunAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "TUN Requires Administrator",
            "TUN 模式需要管理员权限。是否立即以管理员权限重启应用？");

        if (!confirmed)
        {
            return false;
        }

        if (ElevationHelper.TryRestartAsAdministrator())
        {
            return true;
        }

        await _dialogService.ShowErrorAsync(
            "Elevation Failed",
            "无法以管理员权限重启应用。请手动右键“以管理员身份运行”。");
        return false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[index];
    }
}
