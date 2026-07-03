using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CoreController _coreController;
    private readonly IWindowService _windowService;
    private readonly LocalizationService _localizationService;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private bool _isCoreRunning;

    [ObservableProperty]
    private string _coreToggleLabel = "Start Core";

    public bool IsExitRequested { get; private set; }

    public MainWindowViewModel(
        CoreController coreController,
        IWindowService windowService,
        LocalizationService localizationService,
        AppSettingsStore settingsStore,
        IDialogService dialogService)
    {
        _coreController = coreController;
        _windowService = windowService;
        _localizationService = localizationService;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _isCoreRunning = _coreController.IsRunning;
        UpdateCoreLabel();

        _localizationService.LanguageChanged += (_, _) => Dispatch(UpdateCoreLabel);

        _coreController.RunningChanged += (_, isRunning) =>
        {
            Dispatch(() =>
            {
                IsCoreRunning = isRunning;
                UpdateCoreLabel();
            });
        };
    }

    [RelayCommand]
    private void ShowWindow()
    {
        _windowService.ShowMainWindow();
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (!_coreController.IsRunning
            && _settingsStore.Settings.TunEnabled
            && !ElevationHelper.IsRunningAsAdministrator())
        {
            var confirmed = await _dialogService.ShowConfirmAsync(
                "TUN Requires Administrator",
                "TUN 模式需要管理员权限。是否立即以管理员权限重启应用？");

            if (!confirmed)
            {
                return;
            }

            if (ElevationHelper.TryRestartAsAdministrator())
            {
                IsExitRequested = true;
                Application.Current?.Shutdown();
                return;
            }

            await _dialogService.ShowErrorAsync(
                "Elevation Failed",
                "无法以管理员权限重启应用。请手动右键“以管理员身份运行”。");
            return;
        }

        try
        {
            await _coreController.ToggleAsync();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "MainWindowViewModel: Toggle core");
            await _dialogService.ShowErrorAsync("Core Operation Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Exit()
    {
        IsExitRequested = true;
        Application.Current.Shutdown();
    }

    private void UpdateCoreLabel()
    {
        CoreToggleLabel = IsCoreRunning
            ? _localizationService.GetString("Text_StopCore", "Stop Core")
            : _localizationService.GetString("Text_StartCore", "Start Core");
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
}
