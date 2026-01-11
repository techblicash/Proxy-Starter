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
        RulesStore rulesStore)
    {
        _settingsStore = settingsStore;
        _apiClient = apiClient;
        _dialogService = dialogService;
        _rulesStore = rulesStore;
        _selectedMode = settingsStore.Settings.Mode;
        _rulesText = _rulesStore.LoadText();
    }

    [RelayCommand]
    private async Task SaveModeAsync()
    {
        _settingsStore.Settings.Mode = SelectedMode;
        _settingsStore.Save();

        var ok = await _apiClient.SetModeAsync(SelectedMode);
        if (!ok)
        {
            await _dialogService.ShowErrorAsync("Mode Update Failed", "Failed to update mode. Is the core running?");
        }
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        _rulesStore.SaveText(RulesText);
        await _dialogService.ShowInfoAsync("Rules Saved", "Restart the core to apply the updated rules.");
    }

    [RelayCommand]
    private void ReloadRules()
    {
        RulesText = _rulesStore.LoadText();
    }
}
