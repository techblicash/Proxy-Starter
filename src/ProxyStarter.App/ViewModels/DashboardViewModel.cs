using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly CoreController _coreController;
    private readonly TrafficMonitorService _trafficMonitorService;
    private readonly ConnectionMonitorService _connectionMonitorService;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly LocalizationService _localizationService;

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
        IDialogService dialogService,
        LocalizationService localizationService)
    {
        _coreController = coreController;
        _trafficMonitorService = trafficMonitorService;
        _connectionMonitorService = connectionMonitorService;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _localizationService = localizationService;

        Mode = _settingsStore.Settings.Mode;
        LoadNetworkSettings();
        UpdateStatus();
        _coreController.RunningChanged += (_, _) => Dispatch(UpdateStatus);
        _localizationService.LanguageChanged += (_, _) => Dispatch(UpdateStatus);

        _trafficMonitorService.TrafficUpdated += OnTrafficUpdated;
        _trafficMonitorService.Start();

        _connectionMonitorService.StatusUpdated += OnStatusUpdated;
        _connectionMonitorService.Start();
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (!_coreController.IsRunning)
        {
            var corePath = ResolveCorePath(_settingsStore.Settings.CorePath);
            if (!File.Exists(corePath))
            {
                await _dialogService.ShowErrorAsync(
                    "Mihomo Core Not Found",
                    $"Core executable was not found:\n{corePath}\n\nPlease download Mihomo and update Core Path in Settings.");
                return;
            }
        }

        await _coreController.ToggleAsync();
    }

    [RelayCommand]
    private async Task SaveNetworkAsync()
    {
        var settings = _settingsStore.Settings;
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
        Dispatch(() =>
        {
            UploadSpeed = FormatSpeed(snapshot.UploadBytesPerSecond);
            DownloadSpeed = FormatSpeed(snapshot.DownloadBytesPerSecond);
        });
    }

    private void OnStatusUpdated(object? sender, ConnectionStatusSnapshot snapshot)
    {
        Dispatch(() =>
        {
            ActiveNode = snapshot.ActiveNode;
            CurrentAddress = string.IsNullOrWhiteSpace(snapshot.CurrentAddress) ? "Unknown" : snapshot.CurrentAddress;
            UploadTotal = FormatBytes(snapshot.UploadTotal);
            DownloadTotal = FormatBytes(snapshot.DownloadTotal);
        });
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

        Application.Current.Dispatcher.Invoke(action);
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        return FormatBytes(bytesPerSecond) + "/s";
    }

    private static string ResolveCorePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
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
