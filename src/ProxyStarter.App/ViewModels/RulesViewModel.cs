using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class RulesViewModel : ObservableObject
{
    private readonly AppSettingsStore _settingsStore;
    private readonly MihomoApiClient _apiClient;
    private readonly IDialogService _dialogService;
    private readonly RulesStore _rulesStore;
    private readonly LocalizationService _localization;

    [ObservableProperty]
    private string _selectedMode;

    [ObservableProperty]
    private string _rulesText = string.Empty;

    public ObservableCollection<string> AvailableModes { get; } = new()
    {
        "rule",
        "global",
        "direct"
    };

    public RulesViewModel(
        AppSettingsStore settingsStore,
        MihomoApiClient apiClient,
        IDialogService dialogService,
        RulesStore rulesStore,
        LocalizationService localization)
    {
        _settingsStore = settingsStore;
        _apiClient = apiClient;
        _dialogService = dialogService;
        _rulesStore = rulesStore;
        _localization = localization;
        _selectedMode = settingsStore.Settings.Mode;
        _rulesText = _rulesStore.LoadText();
    }

    [RelayCommand]
    private async Task SaveModeAsync()
    {
        try
        {
            _settingsStore.Settings.Mode = SelectedMode;
            _settingsStore.Save();
        }
        catch (System.Exception ex)
        {
            CrashLogger.Log(ex, "RulesViewModel: Save mode");
            await _dialogService.ShowErrorAsync(DialogError.ModeSave, ex.Message);
            return;
        }

        var ok = await _apiClient.SetModeAsync(SelectedMode);
        if (!ok)
        {
            await _dialogService.ShowErrorAsync(DialogError.ModeUpdate);
        }
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        try
        {
            _rulesStore.SaveText(RulesText);
        }
        catch (System.Exception ex)
        {
            CrashLogger.Log(ex, "RulesViewModel: Save custom rules");
            await _dialogService.ShowErrorAsync(DialogError.RulesSave, ex.Message);
            return;
        }

        await _dialogService.ShowInfoAsync(
            _localization.GetString("Text_RulesSaved", "Rules saved"),
            _localization.GetString("Text_RulesRestartHint", "Restart the proxy core to apply the updated rules."));
    }

    [RelayCommand]
    private void ReloadRules()
    {
        RulesText = _rulesStore.LoadText();
    }
}
