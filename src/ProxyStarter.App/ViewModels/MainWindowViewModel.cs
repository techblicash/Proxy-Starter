using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CoreController _coreController;
    private readonly IWindowService _windowService;
    private readonly LocalizationService _localizationService;

    [ObservableProperty]
    private bool _isCoreRunning;

    [ObservableProperty]
    private string _coreToggleLabel = "Start Core";

    public bool IsExitRequested { get; private set; }

    public MainWindowViewModel(
        CoreController coreController,
        IWindowService windowService,
        LocalizationService localizationService)
    {
        _coreController = coreController;
        _windowService = windowService;
        _localizationService = localizationService;
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
        await _coreController.ToggleAsync();
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

        Application.Current.Dispatcher.Invoke(action);
    }
}
