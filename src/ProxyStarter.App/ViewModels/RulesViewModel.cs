using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class RulesViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly AppSettingsStore _settingsStore;
    private readonly CoreController _coreController;
    private readonly SubscriptionService _subscriptionService;
    private readonly IDialogService _dialogService;
    private readonly RulesStore _rulesStore;
    private readonly LocalizationService _localization;

    [ObservableProperty]
    private string _selectedMode;

    [ObservableProperty]
    private IReadOnlyList<SubscriptionProfile> _subscriptionRuleSources = Array.Empty<SubscriptionProfile>();

    [ObservableProperty]
    private string? _selectedSubscriptionRulesId;

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
        CoreController coreController,
        SubscriptionService subscriptionService,
        IDialogService dialogService,
        RulesStore rulesStore,
        LocalizationService localization)
    {
        _settingsStore = settingsStore;
        _coreController = coreController;
        _subscriptionService = subscriptionService;
        _dialogService = dialogService;
        _rulesStore = rulesStore;
        _localization = localization;
        _selectedMode = settingsStore.Settings.Mode;
        _rulesText = _rulesStore.LoadText();
    }

    public void OnPageActivated()
    {
        var sources = new List<SubscriptionProfile>
        {
            new() { Id = string.Empty, Name = _localization.GetString("Text_NoSubscriptionRules", "Use built-in and custom rules") }
        };
        sources.AddRange(_subscriptionService.GetRuleSources());
        SubscriptionRuleSources = sources;
        var savedId = _settingsStore.Settings.SubscriptionRulesProfileId;
        SelectedSubscriptionRulesId = sources.FirstOrDefault(source => source.Id == savedId)?.Id ?? string.Empty;
    }

    public void OnPageDeactivated() { }

    [RelayCommand]
    private async Task SaveModeAsync()
    {
        try
        {
            await _coreController.SetRoutingModeAsync(SelectedMode);
        }
        catch (Exception ex)
        {
            SelectedMode = _settingsStore.Settings.Mode;
            CrashLogger.Log(ex, "RulesViewModel: Save mode");
            await _dialogService.ShowErrorAsync(DialogError.ModeSave, ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveSubscriptionRulesAsync()
    {
        try
        {
            await _coreController.SetSubscriptionRulesAsync(SelectedSubscriptionRulesId ?? string.Empty);
        }
        catch (Exception ex)
        {
            OnPageActivated();
            CrashLogger.Log(ex, "RulesViewModel: Save subscription routing rules");
            await _dialogService.ShowErrorAsync(DialogError.RulesSave, ex.Message);
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
