using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;
using ProxyStarter.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ProxyStarter.App.Views;

public partial class TrayMenuWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MihomoApiClient _apiClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCatalogStore _proxyCatalogStore;

    private readonly List<ProxyNode> _allNodes = new();
    private readonly ObservableCollection<ProxyNode> _filteredNodes = new();

    private bool _suppressSelection;
    private bool _isLoading;
    private string _currentSelectionGroup = string.Empty;

    public TrayMenuWindow(
        MainWindowViewModel viewModel,
        MihomoApiClient apiClient,
        AppSettingsStore settingsStore,
        ProxyCatalogStore proxyCatalogStore)
    {
        _viewModel = viewModel;
        _apiClient = apiClient;
        _settingsStore = settingsStore;
        _proxyCatalogStore = proxyCatalogStore;

        InitializeComponent();
        DataContext = _viewModel;
        NodesList.ItemsSource = _filteredNodes;

        Loaded += async (_, _) =>
        {
            ApplicationThemeManager.Apply(this);
            await LoadNodesAsync().ConfigureAwait(true);
        };

        Deactivated += (_, _) => Close();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowWindowCommand.Execute(null);
        Close();
    }

    private void OnToggleCoreClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ToggleCoreCommand.CanExecute(null))
        {
            _viewModel.ToggleCoreCommand.Execute(null);
        }

        Close();
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ExitCommand.Execute(null);
        Close();
    }

    private async void OnRefreshNodesClicked(object sender, RoutedEventArgs e)
    {
        await LoadNodesAsync().ConfigureAwait(true);
    }

    private async Task LoadNodesAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        NodesStatusText.Text = "Loading...";
        _allNodes.Clear();
        _filteredNodes.Clear();

        var settings = _settingsStore.Settings;
        var groupName = RoutingDefaults.ResolveSelectionGroup(settings.SelectionGroup, settings.UseSubscriptionPolicyGroups);
        _currentSelectionGroup = groupName;
        NodesGroupText.Text = string.IsNullOrWhiteSpace(groupName) ? "" : groupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            NodesStatusText.Text = "Selection group is empty";
            _isLoading = false;
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var snapshot = await _apiClient.GetProxiesAsync(timeout.Token).ConfigureAwait(true);
            if (snapshot is null)
            {
                LoadLocalNodes("Core API unavailable");
                return;
            }

            if (!TryResolveGroup(snapshot, groupName, out var actualGroupName, out var group) || group.All.Count == 0)
            {
                LoadLocalNodes("Core group unavailable");
                return;
            }

            _currentSelectionGroup = actualGroupName;
            NodesGroupText.Text = actualGroupName;
            var active = await _apiClient.GetResolvedSelectedProxyAsync(actualGroupName, timeout.Token).ConfigureAwait(true);
            active ??= group.Now;
            foreach (var name in group.All.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _allNodes.Add(new ProxyNode
                {
                    Name = name,
                    IsActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase)
                });
            }

            ApplyFilter(NodeSearchBox.Text);
            NodesStatusText.Text = $"{_allNodes.Count} nodes";
        }
        catch
        {
            LoadLocalNodes("Core API unavailable");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool TryResolveGroup(
        MihomoProxiesSnapshot snapshot,
        string requestedGroup,
        out string groupName,
        out MihomoProxy group)
    {
        foreach (var candidate in BuildGroupCandidates(snapshot, requestedGroup))
        {
            if (snapshot.Proxies.TryGetValue(candidate, out var candidateGroup) && candidateGroup.All.Count > 0)
            {
                groupName = candidate;
                group = candidateGroup;
                return true;
            }
        }

        groupName = string.Empty;
        group = new MihomoProxy(string.Empty, string.Empty, null, Array.Empty<string>());
        return false;
    }

    private IEnumerable<string> BuildGroupCandidates(MihomoProxiesSnapshot snapshot, string requestedGroup)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Yield(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && yielded.Add(value);
        }

        if (Yield(requestedGroup))
        {
            yield return requestedGroup;
        }

        var configured = _settingsStore.Settings.SelectionGroup;
        if (Yield(configured))
        {
            yield return configured;
        }

        if (Yield(RoutingDefaults.FlatModeSelectionGroup))
        {
            yield return RoutingDefaults.FlatModeSelectionGroup;
        }

        if (Yield("GLOBAL"))
        {
            yield return "GLOBAL";
        }

        foreach (var group in snapshot.Proxies.Values
                     .Where(proxy => proxy.All.Count > 0)
                     .OrderByDescending(proxy => proxy.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(proxy => proxy.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (Yield(group.Name))
            {
                yield return group.Name;
            }
        }
    }

    private void LoadLocalNodes(string reason)
    {
        try
        {
            var nodes = _proxyCatalogStore.LoadNodes()
                .Where(node => !string.IsNullOrWhiteSpace(node.Name))
                .GroupBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (var node in nodes)
            {
                _allNodes.Add(node);
            }

            ApplyFilter(NodeSearchBox.Text);
            NodesStatusText.Text = nodes.Count > 0 ? $"{nodes.Count} local nodes ({reason})" : reason;
        }
        catch
        {
            NodesStatusText.Text = GetString("Text_Stopped", "Stopped");
        }
    }

    private void ApplyFilter(string? query)
    {
        query = (query ?? string.Empty).Trim();
        _filteredNodes.Clear();

        IEnumerable<ProxyNode> source = _allNodes;
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(node => node.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var node in source)
        {
            _filteredNodes.Add(node);
        }
    }

    private void OnNodeSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilter(NodeSearchBox.Text);
    }

    private async void OnNodeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (NodesList.SelectedItem is not ProxyNode node)
        {
            return;
        }

        if (node.IsActive)
        {
            Close();
            return;
        }

        var groupName = string.IsNullOrWhiteSpace(_currentSelectionGroup)
            ? RoutingDefaults.ResolveSelectionGroup(
                _settingsStore.Settings.SelectionGroup,
                _settingsStore.Settings.UseSubscriptionPolicyGroups)
            : _currentSelectionGroup;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            Close();
            return;
        }

        _suppressSelection = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var success = await _apiClient.SetProxySelectionAsync(groupName, node.Name, timeout.Token).ConfigureAwait(true);
            if (!success)
            {
                return;
            }

            foreach (var item in _allNodes)
            {
                item.IsActive = string.Equals(item.Name, node.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "TrayMenuWindow: Select node");
        }
        finally
        {
            _suppressSelection = false;
            Close();
        }
    }

    private static string GetString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
