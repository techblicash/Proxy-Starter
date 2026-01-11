using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _settingsStore;
    private readonly AppThemeService _themeService;
    private readonly AutoStartService _autoStartService;
    private readonly CoreController _coreController;
    private readonly LocalizationService _localizationService;
    private readonly QosPolicyService _qosPolicyService;
    private readonly IDialogService _dialogService;

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
    private bool _autoStart;

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private string _updateFeedUrl;

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
        AppThemeService themeService,
        AutoStartService autoStartService,
        CoreController coreController,
        LocalizationService localizationService,
        QosPolicyService qosPolicyService,
        IDialogService dialogService)
    {
        _settingsStore = settingsStore;
        _themeService = themeService;
        _autoStartService = autoStartService;
        _coreController = coreController;
        _localizationService = localizationService;
        _qosPolicyService = qosPolicyService;
        _dialogService = dialogService;

        var settings = settingsStore.Settings;
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
        _autoStart = _autoStartService.IsEnabled();
        _autoUpdate = settings.AutoUpdate;
        _updateFeedUrl = settings.UpdateFeedUrl;
        _theme = settings.Theme;
        _paneAcrylicOpacity = settings.PaneAcrylicOpacity;
        _contentAcrylicOpacity = settings.ContentAcrylicOpacity;
        _fontSize = settings.FontSize;
        _blockedSites = settings.BlockedSites ?? string.Empty;
        _downloadLimitKbps = settings.DownloadLimitKbps;
        _uploadLimitKbps = settings.UploadLimitKbps;
        _language = string.IsNullOrWhiteSpace(settings.Language) ? "English" : settings.Language;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = _settingsStore.Settings;
        var wasRunning = _coreController.IsRunning;
        var needsRestart =
            settings.CorePath != CorePath
            || settings.ConfigPath != ConfigPath
            || settings.ApiSecret != ApiSecret
            || settings.AllowLan != AllowLan
            || settings.LogLevel != LogLevel
            || settings.BlockedSites != BlockedSites;

        settings.CorePath = CorePath;
        settings.ConfigPath = ConfigPath;
        settings.ApiSecret = ApiSecret;
        settings.AllowLan = AllowLan;
        settings.LogLevel = LogLevel;
        settings.AutoStart = AutoStart;
        settings.AutoUpdate = AutoUpdate;
        settings.UpdateFeedUrl = UpdateFeedUrl;
        settings.Theme = Theme;
        settings.PaneAcrylicOpacity = System.Math.Clamp(PaneAcrylicOpacity, 0, 255);
        settings.ContentAcrylicOpacity = System.Math.Clamp(ContentAcrylicOpacity, 0, 255);
        settings.FontSize = System.Math.Clamp(FontSize, 10, 24);
        settings.BlockedSites = BlockedSites ?? string.Empty;
        settings.DownloadLimitKbps = System.Math.Max(0, DownloadLimitKbps);
        settings.UploadLimitKbps = System.Math.Max(0, UploadLimitKbps);
        settings.Language = Language;

        _settingsStore.Save();
        _autoStartService.Apply(AutoStart);
        _themeService.ApplyTheme(Theme);
        _localizationService.ApplyLanguage(Language);

        var qosResult = await _qosPolicyService.ApplyAsync(settings);
        if (!qosResult.IsSuccess)
        {
            await _dialogService.ShowErrorAsync("QoS Apply Failed", qosResult.Message);
        }

        if (wasRunning && needsRestart)
        {
            await _coreController.StopAsync();
            await _coreController.StartAsync();
        }
    }

    [RelayCommand]
    private void Reload()
    {
        _settingsStore.Reload();
        var settings = _settingsStore.Settings;

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
        AutoStart = _autoStartService.IsEnabled();
        AutoUpdate = settings.AutoUpdate;
        UpdateFeedUrl = settings.UpdateFeedUrl;
        Theme = settings.Theme;
        PaneAcrylicOpacity = settings.PaneAcrylicOpacity;
        ContentAcrylicOpacity = settings.ContentAcrylicOpacity;
        FontSize = settings.FontSize;
        BlockedSites = settings.BlockedSites ?? string.Empty;
        DownloadLimitKbps = settings.DownloadLimitKbps;
        UploadLimitKbps = settings.UploadLimitKbps;
        Language = string.IsNullOrWhiteSpace(settings.Language) ? "English" : settings.Language;

        _themeService.ApplyTheme(Theme);
        _localizationService.ApplyLanguage(Language);
    }
}
