using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class ProfilesViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly SubscriptionStore _subscriptionStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly SubscriptionCacheStore _cacheStore;
    private readonly MihomoApiClient _apiClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _activeTimer;
    private readonly HashSet<SubscriptionProfile> _attachedProfiles = new(ReferenceEqualityComparer.Instance);
    private bool _isActive;
    private bool _suppressProfilePersistence;

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
        Profiles.CollectionChanged += OnProfilesCollectionChanged;
        foreach (var profile in Profiles)
        {
            AttachProfile(profile);
        }

        _activeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _activeTimer.Tick += async (_, _) => await UpdateActiveProfileAsync();
        // Timer runs only while the Profiles page is visible.
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
        PersistProfiles(rebuildCatalog: false);

        try
        {
            await _subscriptionService.RefreshProfileAsync(profile);
            PersistProfiles(rebuildCatalog: false);
            _subscriptionService.RebuildCatalogFromCache(Profiles);

            ReplaceProfiles(_subscriptionStore.Load());

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
            PersistProfiles(rebuildCatalog: false);
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

        _suppressProfilePersistence = true;
        try
        {
            SelectedProfile.Name = updated.Name;
            SelectedProfile.Url = updated.Url;
            SelectedProfile.AutoUpdateEnabled = updated.AutoUpdateEnabled;
            SelectedProfile.AutoUpdateIntervalMinutes = updated.AutoUpdateIntervalMinutes;
        }
        finally
        {
            _suppressProfilePersistence = false;
        }

        PersistProfiles(rebuildCatalog: true);
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

            PersistProfiles(rebuildCatalog: true);
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
        ReplaceProfiles(_subscriptionStore.Load());

        await UpdateActiveProfileAsync();
    }

    [RelayCommand]
    private void SaveProfiles()
    {
        PersistProfiles(rebuildCatalog: true);
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
        PersistProfiles(rebuildCatalog: true);
        await UpdateActiveProfileAsync();
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<SubscriptionProfile>())
            {
                DetachProfile(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<SubscriptionProfile>())
            {
                AttachProfile(item);
            }
        }
    }

    private void AttachProfile(SubscriptionProfile profile)
    {
        if (_attachedProfiles.Add(profile))
        {
            profile.PropertyChanged += OnProfilePropertyChanged;
        }
    }

    private void DetachProfile(SubscriptionProfile profile)
    {
        if (_attachedProfiles.Remove(profile))
        {
            profile.PropertyChanged -= OnProfilePropertyChanged;
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressProfilePersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(SubscriptionProfile.IsEnabled)
            or nameof(SubscriptionProfile.AutoUpdateEnabled)
            or nameof(SubscriptionProfile.AutoUpdateIntervalMinutes)
            or nameof(SubscriptionProfile.Name)
            or nameof(SubscriptionProfile.Url))
        {
            PersistProfiles(rebuildCatalog: true);
        }
    }

    private void PersistProfiles(bool rebuildCatalog)
    {
        if (_suppressProfilePersistence)
        {
            return;
        }

        _subscriptionStore.Save(Profiles.ToList());
        if (rebuildCatalog)
        {
            _subscriptionService.RebuildCatalogFromCache(Profiles);
        }
    }

    private void ReplaceProfiles(IEnumerable<SubscriptionProfile> profiles)
    {
        _suppressProfilePersistence = true;
        try
        {
            foreach (var profile in _attachedProfiles.ToList())
            {
                DetachProfile(profile);
            }

            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }
        }
        finally
        {
            _suppressProfilePersistence = false;
        }
    }

    private async Task UpdateActiveProfileAsync()
    {
        if (!_isActive)
        {
            return;
        }

        var settings = _settingsStore.Settings;
        var groupName = RoutingDefaults.ResolveSelectionGroup(settings.SelectionGroup, settings.UseSubscriptionPolicyGroups);
        string? activeNode = null;
        try
        {
            activeNode = await _apiClient.GetResolvedSelectedProxyAsync(groupName);
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

    public void OnPageActivated()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;
        _activeTimer.Start();
        _ = UpdateActiveProfileAsync();
    }

    public void OnPageDeactivated()
    {
        _isActive = false;
        _activeTimer.Stop();
    }
}
