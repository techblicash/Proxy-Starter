using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly SubscriptionStore _subscriptionStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly SubscriptionCacheStore _cacheStore;
    private readonly MihomoApiClient _apiClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _activeTimer;

    [ObservableProperty]
    private SubscriptionProfile? _selectedProfile;

    public ObservableCollection<SubscriptionProfile> Profiles { get; }

    public ProfilesViewModel(
        SubscriptionStore subscriptionStore,
        SubscriptionService subscriptionService,
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionCacheStore cacheStore,
        MihomoApiClient apiClient,
        AppSettingsStore settingsStore,
        IDialogService dialogService)
    {
        _subscriptionStore = subscriptionStore;
        _subscriptionService = subscriptionService;
        _proxyCatalogStore = proxyCatalogStore;
        _cacheStore = cacheStore;
        _apiClient = apiClient;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        Profiles = new ObservableCollection<SubscriptionProfile>(_subscriptionStore.Load());

        _activeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _activeTimer.Tick += async (_, _) => await UpdateActiveProfileAsync();
        _activeTimer.Start();

        _ = UpdateActiveProfileAsync();
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        var profile = _dialogService.ShowAddSubscriptionDialog();
        if (profile is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            if (Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri))
            {
                profile.Name = uri.Host;
            }
        }

        Profiles.Add(profile);
        _subscriptionStore.Save(Profiles.ToList());

        try
        {
            await _subscriptionService.RefreshProfileAsync(profile);
            _subscriptionStore.Save(Profiles.ToList());
            _subscriptionService.RebuildCatalogFromCache(Profiles);

            Profiles.Clear();
            foreach (var saved in _subscriptionStore.Load())
            {
                Profiles.Add(saved);
            }

            await UpdateActiveProfileAsync();
            await _dialogService.ShowInfoAsync("Subscription Added", $"Fetched {profile.NodeCount} nodes.");
        }
        catch (System.Exception ex)
        {
            var message = ex.Message;
            if (ex.InnerException is not null)
            {
                message += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            }

            await _dialogService.ShowErrorAsync("Subscription Download Failed", message);
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            try
            {
                await _dialogService.ShowErrorAsync("No Selection", "Please select a subscription first.");
            }
            catch
            {
            }

            return;
        }

        try
        {
            await _subscriptionService.RefreshProfileAsync(SelectedProfile);
            _subscriptionStore.Save(Profiles.ToList());
            _subscriptionService.RebuildCatalogFromCache(Profiles);
            await UpdateActiveProfileAsync();

            await _dialogService.ShowInfoAsync("Subscription Updated", $"Fetched {SelectedProfile.NodeCount} nodes.");
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (ex.InnerException is not null)
            {
                message += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            }

            try
            {
                await _dialogService.ShowErrorAsync("Subscription Download Failed", message);
            }
            catch
            {
            }
        }
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var updated = _dialogService.ShowEditSubscriptionDialog(SelectedProfile);
        if (updated is null)
        {
            return;
        }

        SelectedProfile.Name = updated.Name;
        SelectedProfile.Url = updated.Url;
        SelectedProfile.AutoUpdateEnabled = updated.AutoUpdateEnabled;
        SelectedProfile.AutoUpdateIntervalMinutes = updated.AutoUpdateIntervalMinutes;

        _subscriptionStore.Save(Profiles.ToList());
        _subscriptionService.RebuildCatalogFromCache(Profiles);
        await UpdateActiveProfileAsync();
    }

    [RelayCommand]
    private async Task EditFileSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var profile = SelectedProfile;
        var content = _cacheStore.LoadRaw(profile.Id);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = _cacheStore.BuildEditableYaml(profile.Id);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            try
            {
                await _dialogService.ShowErrorAsync("No Cache", "Please update the subscription first.");
            }
            catch
            {
            }

            return;
        }

        var updated = _dialogService.ShowEditSubscriptionFileDialog(profile.Name, content);
        if (updated is null)
        {
            return;
        }

        try
        {
            var result = await _subscriptionService.ApplyProfileContentAsync(profile, updated);
            if (result.Nodes.Count == 0)
            {
                var confirm = await _dialogService.ShowConfirmAsync("Parsed 0 nodes",
                    "The edited content parsed 0 nodes. Save anyway?");
                if (!confirm)
                {
                    return;
                }
            }

            _subscriptionStore.Save(Profiles.ToList());
            _subscriptionService.RebuildCatalogFromCache(Profiles);
            await UpdateActiveProfileAsync();
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (ex.InnerException is not null)
            {
                message += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            }

            try
            {
                await _dialogService.ShowErrorAsync("Save Failed", message);
            }
            catch
            {
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await _subscriptionService.RefreshAllAsync();
        Profiles.Clear();
        foreach (var profile in _subscriptionStore.Load())
        {
            Profiles.Add(profile);
        }

        await UpdateActiveProfileAsync();
    }

    [RelayCommand]
    private void SaveProfiles()
    {
        _subscriptionStore.Save(Profiles.ToList());
        _subscriptionService.RebuildCatalogFromCache(Profiles);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var confirm = await _dialogService.ShowConfirmAsync("Remove Subscription",
            $"Remove {SelectedProfile.Name}?");
        if (!confirm)
        {
            return;
        }

        Profiles.Remove(SelectedProfile);
        _subscriptionStore.Save(Profiles.ToList());
        _subscriptionService.RebuildCatalogFromCache(Profiles);
        await UpdateActiveProfileAsync();
    }

    private async Task UpdateActiveProfileAsync()
    {
        var groupName = _settingsStore.Settings.SelectionGroup;
        string? activeNode = null;
        try
        {
            activeNode = await _apiClient.GetSelectedProxyAsync(groupName);
        }
        catch
        {
        }
        var activeProfileId = string.Empty;

        if (!string.IsNullOrWhiteSpace(activeNode))
        {
            try
            {
                var nodes = _proxyCatalogStore.LoadNodes();
                var match = nodes.FirstOrDefault(node =>
                    string.Equals(node.Name, activeNode, StringComparison.OrdinalIgnoreCase));
                activeProfileId = match?.SourceId ?? string.Empty;
            }
            catch
            {
            }
        }

        foreach (var profile in Profiles)
        {
            profile.IsActive = !string.IsNullOrWhiteSpace(activeProfileId)
                               && string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
