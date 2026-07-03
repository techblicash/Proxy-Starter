using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _settingsStore;
    private readonly MihomoLogService _logService;
    private readonly AppThemeService _themeService;
    private readonly AutoStartService _autoStartService;
    private readonly CoreController _coreController;
    private readonly LocalizationService _localizationService;
    private readonly QosPolicyService _qosPolicyService;
    private readonly IDialogService _dialogService;
    private readonly CoreDownloadService _coreDownloadService;
    private readonly SoftwareUpdateService _softwareUpdateService;
    private bool _isLoading;

    [ObservableProperty]
    private string _coreType;

    [ObservableProperty]
    private string _corePath;

    [ObservableProperty]
    private string _configPath;

    [ObservableProperty]
    private int _mixedPort;

    [ObservableProperty]
    private int _httpPort;

    [ObservableProperty]
    private int _socksPort;

    [ObservableProperty]
    private int _apiPort;

    [ObservableProperty]
    private string _apiSecret;

    [ObservableProperty]
    private bool _allowLan;

    [ObservableProperty]
    private bool _tunEnabled;

    [ObservableProperty]
    private string _logLevel;

    [ObservableProperty]
    private int _logRetentionCount;

    [ObservableProperty]
    private bool _useSubscriptionPolicyGroups;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _updateFeedUrl;

    [ObservableProperty]
    private string _currentVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckSoftwareUpdateCommand))]
    private bool _isCheckingSoftwareUpdate;

    [ObservableProperty]
    private string _softwareUpdateStatus;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private int _paneAcrylicOpacity;

    [ObservableProperty]
    private int _contentAcrylicOpacity;

    [ObservableProperty]
    private int _fontSize;

    [ObservableProperty]
    private string _blockedSites;

    [ObservableProperty]
    private int _downloadLimitKbps;

    [ObservableProperty]
    private int _uploadLimitKbps;

    [ObservableProperty]
    private string _language;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAllCoresCommand))]
    private bool _isCoreDownloading;

    [ObservableProperty]
    private string _coreDownloadStatus;

    public ObservableCollection<CoreDefinition> CoreTypes { get; } = new(CoreCatalog.All);

    public ObservableCollection<string> LogLevels { get; } = new()
    {
        "debug",
        "info",
        "warning",
        "error"
    };

    public ObservableCollection<string> Themes { get; } = new()
    {
        "Light",
        "Dark",
        "System"
    };

    public ObservableCollection<string> Languages { get; } = new()
    {
        "English",
        "中文"
    };

    public SettingsViewModel(
        AppSettingsStore settingsStore,
        MihomoLogService logService,
        AppThemeService themeService,
        AutoStartService autoStartService,
        CoreController coreController,
        LocalizationService localizationService,
        QosPolicyService qosPolicyService,
        IDialogService dialogService,
        CoreDownloadService coreDownloadService,
        SoftwareUpdateService softwareUpdateService)
    {
        _settingsStore = settingsStore;
        _logService = logService;
        _themeService = themeService;
        _autoStartService = autoStartService;
        _coreController = coreController;
        _localizationService = localizationService;
        _qosPolicyService = qosPolicyService;
        _dialogService = dialogService;
        _coreDownloadService = coreDownloadService;
        _softwareUpdateService = softwareUpdateService;

        var settings = settingsStore.Settings;
        _coreType = settings.CoreType;
        _corePath = settings.CorePath;
        _configPath = settings.ConfigPath;
        _mixedPort = settings.MixedPort;
        _httpPort = settings.HttpPort;
        _socksPort = settings.SocksPort;
        _apiPort = settings.ApiPort;
        _apiSecret = settings.ApiSecret;
        _allowLan = settings.AllowLan;
        _tunEnabled = settings.TunEnabled;
        _logLevel = settings.LogLevel;
        _logRetentionCount = settings.LogRetentionCount;
        _useSubscriptionPolicyGroups = settings.UseSubscriptionPolicyGroups;
        _autoStart = _autoStartService.IsEnabled();
        _updateFeedUrl = settings.UpdateFeedUrl ?? string.Empty;
        _currentVersion = _softwareUpdateService.CurrentVersion;
        _softwareUpdateStatus = $"Current version: {_currentVersion}";
        _theme = settings.Theme;
        _paneAcrylicOpacity = settings.PaneAcrylicOpacity;
        _contentAcrylicOpacity = settings.ContentAcrylicOpacity;
        _fontSize = settings.FontSize;
        _blockedSites = settings.BlockedSites ?? string.Empty;
        _downloadLimitKbps = settings.DownloadLimitKbps;
        _uploadLimitKbps = settings.UploadLimitKbps;
        _language = string.IsNullOrWhiteSpace(settings.Language) ? "English" : settings.Language;
        _coreDownloadStatus = BuildInstalledCoreStatus();
    }

    partial void OnCoreTypeChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        var core = CoreCatalog.Get(value);
        if (CoreCatalog.IsDefaultPath(CorePath))
        {
            CorePath = core.RelativeExecutablePath;
        }

        ConfigPath = core.DefaultConfigPath;
        CoreDownloadStatus = BuildInstalledCoreStatus();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var settings = _settingsStore.Settings;
            var wasRunning = _coreController.IsRunning;
            var needsRestart =
                settings.CoreType != CoreType
                || settings.CorePath != CorePath
                || settings.ConfigPath != ConfigPath
                || settings.MixedPort != MixedPort
                || settings.HttpPort != HttpPort
                || settings.SocksPort != SocksPort
                || settings.ApiPort != ApiPort
                || settings.ApiSecret != ApiSecret
                || settings.AllowLan != AllowLan
                || settings.LogLevel != LogLevel
                || settings.BlockedSites != BlockedSites
                || settings.TunEnabled != TunEnabled
                || settings.UseSubscriptionPolicyGroups != UseSubscriptionPolicyGroups;
            var needsLogReconnect =
                settings.LogLevel != LogLevel
                || settings.ApiSecret != ApiSecret
                || settings.ApiPort != ApiPort;

            settings.CoreType = CoreType;
            settings.CorePath = CorePath;
            settings.ConfigPath = ConfigPath;
            settings.MixedPort = MixedPort;
            settings.HttpPort = HttpPort;
            settings.SocksPort = SocksPort;
            settings.ApiPort = ApiPort;
            settings.ApiSecret = ApiSecret;
            settings.AllowLan = AllowLan;
            settings.TunEnabled = TunEnabled;
            settings.LogLevel = LogLevel;
            settings.LogRetentionCount = Math.Clamp(LogRetentionCount, 1, MihomoLogService.MaxRetentionLimit);
            settings.UseSubscriptionPolicyGroups = UseSubscriptionPolicyGroups;
            settings.AutoStart = AutoStart;
            settings.UpdateFeedUrl = UpdateFeedUrl ?? string.Empty;
            settings.Theme = Theme;
            settings.PaneAcrylicOpacity = Math.Clamp(PaneAcrylicOpacity, 0, 255);
            settings.ContentAcrylicOpacity = Math.Clamp(ContentAcrylicOpacity, 0, 255);
            settings.FontSize = Math.Clamp(FontSize, 10, 24);
            settings.BlockedSites = BlockedSites ?? string.Empty;
            settings.DownloadLimitKbps = Math.Max(0, DownloadLimitKbps);
            settings.UploadLimitKbps = Math.Max(0, UploadLimitKbps);
            settings.Language = Language;

            _settingsStore.Save();
            _autoStartService.Apply(AutoStart);
            _themeService.ApplyTheme(Theme);
            _localizationService.ApplyLanguage(Language);
            _logService.TrimToRetention();

            if (needsLogReconnect)
            {
                _logService.Stop();
                _logService.Start();
            }

            var qosResult = await _qosPolicyService.ApplyAsync(settings);
            if (!qosResult.IsSuccess)
            {
                await _dialogService.ShowErrorAsync("QoS Apply Failed", qosResult.Message);
            }

            if (wasRunning && needsRestart)
            {
                if (settings.TunEnabled && !ElevationHelper.IsRunningAsAdministrator())
                {
                    var relaunched = await RequestElevationForTunAsync();
                    if (relaunched)
                    {
                        Application.Current?.Shutdown();
                        return;
                    }

                    await _dialogService.ShowErrorAsync(
                        "Restart Required",
                        "TUN is enabled. Settings were saved. Restart as administrator to apply core changes.");
                    return;
                }

                await _coreController.StopAsync();
                await _coreController.StartAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SettingsViewModel: Save");
            await _dialogService.ShowErrorAsync("Settings Save Failed", ex.Message);
            Reload();
        }
    }

    [RelayCommand]
    private void Reload()
    {
        _isLoading = true;
        try
        {
            _settingsStore.Reload();
            var settings = _settingsStore.Settings;

            CoreType = settings.CoreType;
            CorePath = settings.CorePath;
            ConfigPath = settings.ConfigPath;
            MixedPort = settings.MixedPort;
            HttpPort = settings.HttpPort;
            SocksPort = settings.SocksPort;
            ApiPort = settings.ApiPort;
            ApiSecret = settings.ApiSecret;
            AllowLan = settings.AllowLan;
            TunEnabled = settings.TunEnabled;
            LogLevel = settings.LogLevel;
            LogRetentionCount = settings.LogRetentionCount;
            UseSubscriptionPolicyGroups = settings.UseSubscriptionPolicyGroups;
            AutoStart = _autoStartService.IsEnabled();
            UpdateFeedUrl = settings.UpdateFeedUrl ?? string.Empty;
            CurrentVersion = _softwareUpdateService.CurrentVersion;
            SoftwareUpdateStatus = $"Current version: {CurrentVersion}";
            Theme = settings.Theme;
            PaneAcrylicOpacity = settings.PaneAcrylicOpacity;
            ContentAcrylicOpacity = settings.ContentAcrylicOpacity;
            FontSize = settings.FontSize;
            BlockedSites = settings.BlockedSites ?? string.Empty;
            DownloadLimitKbps = settings.DownloadLimitKbps;
            UploadLimitKbps = settings.UploadLimitKbps;
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "English" : settings.Language;
            CoreDownloadStatus = BuildInstalledCoreStatus();
        }
        finally
        {
            _isLoading = false;
        }

        _themeService.ApplyTheme(Theme);
        _localizationService.ApplyLanguage(Language);
    }

    [RelayCommand(CanExecute = nameof(CanDownloadCore))]
    private async Task DownloadSelectedCoreAsync()
    {
        await DownloadCoresAsync(new[] { CoreCatalog.Get(CoreType) });
    }

    [RelayCommand(CanExecute = nameof(CanDownloadCore))]
    private async Task DownloadAllCoresAsync()
    {
        await DownloadCoresAsync(CoreCatalog.All);
    }

    private bool CanDownloadCore()
    {
        return !IsCoreDownloading;
    }

    [RelayCommand(CanExecute = nameof(CanCheckSoftwareUpdate))]
    private async Task CheckSoftwareUpdateAsync()
    {
        if (IsCheckingSoftwareUpdate)
        {
            return;
        }

        IsCheckingSoftwareUpdate = true;
        try
        {
            SetSoftwareUpdateStatus("Checking for updates...");
            var result = await _softwareUpdateService.CheckAndPrepareUpdateAsync(
                UpdateFeedUrl,
                progress => SetSoftwareUpdateStatus($"Downloading update... {progress}%"));

            CurrentVersion = result.CurrentVersion;
            SetSoftwareUpdateStatus(result.AvailableVersion is null
                ? result.Message
                : $"{result.Message} Latest: {result.AvailableVersion}");

            switch (result.Status)
            {
                case ProxyStarter.App.Services.SoftwareUpdateStatus.FeedNotConfigured:
                    await _dialogService.ShowErrorAsync("Update Feed Missing", result.Message);
                    break;
                case ProxyStarter.App.Services.SoftwareUpdateStatus.NotInstalled:
                    await _dialogService.ShowInfoAsync("Updater Unavailable", result.Message);
                    break;
                case ProxyStarter.App.Services.SoftwareUpdateStatus.NoUpdate:
                    await _dialogService.ShowInfoAsync("No Updates", result.Message);
                    break;
                case ProxyStarter.App.Services.SoftwareUpdateStatus.PendingRestart:
                case ProxyStarter.App.Services.SoftwareUpdateStatus.UpdateReady:
                    var versionText = string.IsNullOrWhiteSpace(result.AvailableVersion)
                        ? "the downloaded update"
                        : $"version {result.AvailableVersion}";
                    var restart = await _dialogService.ShowConfirmAsync(
                        "Update Ready",
                        $"{versionText} is ready. Restart Proxy Starter now to apply it?");
                    if (!restart)
                    {
                        return;
                    }

                    try
                    {
                        await _coreController.StopAsync();
                    }
                    catch
                    {
                    }

                    _softwareUpdateService.ApplyPreparedUpdateAndRestart();
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SettingsViewModel: Check software update");
            SetSoftwareUpdateStatus("Update check failed.");
            await _dialogService.ShowErrorAsync("Update Check Failed", ex.Message);
        }
        finally
        {
            IsCheckingSoftwareUpdate = false;
        }
    }

    private bool CanCheckSoftwareUpdate()
    {
        return !IsCheckingSoftwareUpdate;
    }

    private void SetSoftwareUpdateStatus(string value)
    {
        if (Application.Current is null)
        {
            SoftwareUpdateStatus = value;
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() => SoftwareUpdateStatus = value);
    }

    private async Task DownloadCoresAsync(IEnumerable<CoreDefinition> cores)
    {
        if (IsCoreDownloading)
        {
            return;
        }

        IsCoreDownloading = true;
        try
        {
            var installed = new List<CoreDownloadResult>();
            foreach (var core in cores)
            {
                CoreDownloadStatus = $"Downloading {core.DisplayName}...";
                installed.Add(await _coreDownloadService.DownloadAsync(core));
            }

            var selectedCore = CoreCatalog.Get(CoreType);
            if (CoreCatalog.IsDefaultPath(CorePath))
            {
                CorePath = selectedCore.RelativeExecutablePath;
            }

            CoreDownloadStatus = BuildInstalledCoreStatus();
            var message = string.Join(Environment.NewLine, installed.Select(result =>
                $"{result.Core.DisplayName} {result.Version}: {result.InstalledPath}"));
            await _dialogService.ShowInfoAsync("Core Download Complete", message);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SettingsViewModel: Download core");
            CoreDownloadStatus = BuildInstalledCoreStatus();
            await _dialogService.ShowErrorAsync("Core Download Failed", ex.Message);
        }
        finally
        {
            IsCoreDownloading = false;
        }
    }

    private static string BuildInstalledCoreStatus()
    {
        var parts = CoreCatalog.All.Select(core =>
        {
            var path = ProxyCorePath.ResolveExecutable(core.RelativeExecutablePath);
            return $"{core.DisplayName}: {(File.Exists(path) ? "installed" : "missing")}";
        });

        return string.Join("  |  ", parts);
    }

    private async Task<bool> RequestElevationForTunAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "TUN Requires Administrator",
            "TUN mode requires administrator privileges. Restart as administrator now?");

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
            "Could not restart with administrator privileges. Please run the app as Administrator manually.");
        return false;
    }
}
