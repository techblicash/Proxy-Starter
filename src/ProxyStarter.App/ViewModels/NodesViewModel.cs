using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class NodesViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly SubscriptionStore _subscriptionStore;
    private readonly MihomoApiClient _apiClient;
    private readonly LatencyTestService _latencyTestService;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _activeTimer;
    private bool _isActive;
    private readonly Dictionary<string, List<ProxyNode>> _nodeLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProxyNode>> _displayLookup = new(StringComparer.OrdinalIgnoreCase);
    private List<ProxyNode> _allNodes = new();
    private IReadOnlyList<ProxyNode> _currentDisplayNodes = Array.Empty<ProxyNode>();
    private Dictionary<string, string> _sourceNames = new(StringComparer.OrdinalIgnoreCase);
    private int _groupsLoadInProgress;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _rowsCts;
    private CancellationTokenSource? _selectionSaveCts;
    private string? _activeNodeName;
    private string? _activeGroupName;
    private int _activeUpdateInProgress;

    [ObservableProperty]
    private ProxyNode? _selectedNode;

    [ObservableProperty]
    private bool _isTestingAll;

    [ObservableProperty]
    private string _selectedGroup;

    [ObservableProperty]
    private int _cardsPerRow = 2;

    [ObservableProperty]
    private bool _useSubscriptionPolicyGroups = true;

    public ObservableRangeCollection<ProxyNode> Nodes { get; } = new();
    public ObservableRangeCollection<NodeCardRow> NodeRows { get; } = new();
    public ObservableRangeCollection<string> Groups { get; } = new();
    public bool ShowSelectionGroup => UseSubscriptionPolicyGroups;

    public double SavedScrollOffset { get; set; }

    public NodesViewModel(
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionStore subscriptionStore,
        MihomoApiClient apiClient,
        LatencyTestService latencyTestService,
        AppSettingsStore settingsStore,
        IDialogService dialogService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _subscriptionStore = subscriptionStore;
        _apiClient = apiClient;
        _latencyTestService = latencyTestService;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _selectedGroup = settingsStore.Settings.SelectionGroup;
        _useSubscriptionPolicyGroups = settingsStore.Settings.UseSubscriptionPolicyGroups;

        _ = LoadFromCatalogAsync();

        _activeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _activeTimer.Tick += async (_, _) => await UpdateActiveNodeAsync();
        // Timer runs only while the Nodes page is visible.
    }

    partial void OnCardsPerRowChanged(int value)
    {
        if (value < 1)
        {
            return;
        }

        RefreshRowsFromCurrent();
    }

    partial void OnUseSubscriptionPolicyGroupsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSelectionGroup));
    }

    partial void OnSelectedGroupChanged(string value)
    {
        if (!UseSubscriptionPolicyGroups)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        QueueSelectionGroupSave(value);

        if (_isActive)
        {
            _ = RefreshNodeRowsAsync();
            _ = UpdateActiveNodeAsync();
        }
    }

    [RelayCommand]
    private void RefreshNodes()
    {
        _ = LoadFromCatalogAsync();
    }

    [RelayCommand]
    private async Task SelectNodeAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var groupName = GetGroupName();
        bool success;
        try
        {
            success = await _apiClient.SetProxySelectionAsync(groupName, SelectedNode.Name);
        }
        catch
        {
            success = false;
        }

        if (!success)
        {
            try
            {
                await _dialogService.ShowErrorAsync("Selection Failed", "Failed to update proxy selection. Is the core running?");
            }
            catch
            {
            }
            return;
        }

        await UpdateActiveNodeAsync();
    }

    [RelayCommand]
    private async Task TcpTestAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var latency = await _latencyTestService.TestTcpAsync(SelectedNode.Address, SelectedNode.Port);
        UpdateLatency(SelectedNode, latency);
    }

    [RelayCommand]
    private async Task ConnectTestAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        int latency;
        try
        {
            latency = await _latencyTestService.TestProxyDelayAsync(SelectedNode.Name);
        }
        catch
        {
            latency = -1;
        }

        UpdateLatency(SelectedNode, latency);
    }

    [RelayCommand]
    private void PickNode(ProxyNode? node)
    {
        if (node is null)
        {
            return;
        }

        SelectedNode = node;
    }

    [RelayCommand]
    private async Task SelectNodeFromCardAsync(ProxyNode? node)
    {
        if (node is null)
        {
            return;
        }

        SelectedNode = node;

        var groupName = GetGroupName();
        bool success;
        try
        {
            success = await _apiClient.SetProxySelectionAsync(groupName, node.Name);
        }
        catch
        {
            success = false;
        }

        if (!success)
        {
            try
            {
                await _dialogService.ShowErrorAsync("Selection Failed", "Failed to update proxy selection. Is the core running?");
            }
            catch
            {
            }
            return;
        }

        await UpdateActiveNodeAsync();
    }

    [RelayCommand]
    private async Task TcpTestNodeAsync(ProxyNode? node)
    {
        if (node is null)
        {
            return;
        }

        var latency = await _latencyTestService.TestTcpAsync(node.Address, node.Port);
        UpdateLatency(node, latency);
    }

    [RelayCommand]
    private async Task ConnectTestNodeAsync(ProxyNode? node)
    {
        if (node is null)
        {
            return;
        }

        int latency;
        try
        {
            latency = await _latencyTestService.TestProxyDelayAsync(node.Name);
        }
        catch
        {
            latency = -1;
        }

        UpdateLatency(node, latency);
    }

    [RelayCommand]
    private async Task TcpTestAllAsync()
    {
        await TestAllAsync(node => _latencyTestService.TestTcpAsync(node.Address, node.Port));
    }

    [RelayCommand]
    private async Task ConnectTestAllAsync()
    {
        await TestAllAsync(node => _latencyTestService.TestProxyDelayAsync(node.Name));
    }

    private async Task LoadFromCatalogAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        List<ProxyNode> nodes;
        Dictionary<string, string> sourceNames;
        try
        {
            var catalog = await Task.Run(() =>
            {
                var loadedNodes = _proxyCatalogStore.LoadNodes().ToList();
                var names = _subscriptionStore.Load()
                    .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                    .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Name,
                        StringComparer.OrdinalIgnoreCase);

                return (Nodes: loadedNodes, SourceNames: names);
            }, cts.Token);
            nodes = catalog.Nodes;
            sourceNames = catalog.SourceNames;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        _allNodes = nodes;
        _sourceNames = sourceNames;
        RebuildNodeLookup(nodes);

        void ApplyUi()
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            Nodes.ReplaceRange(nodes);
            LoadGroupsFromCatalog();
        }

        if (Application.Current is null)
        {
            ApplyUi();
        }
        else
        {
            try
            {
                _ = Application.Current.Dispatcher.BeginInvoke((Action)ApplyUi, DispatcherPriority.Background);
            }
            catch
            {
            }
        }

        await RefreshNodeRowsAsync().ConfigureAwait(false);
        await UpdateActiveNodeAsync().ConfigureAwait(false);
    }

    private void LoadGroupsFromCatalog()
    {
        SyncPolicyGroupModeFromSettings();

        if (!UseSubscriptionPolicyGroups)
        {
            var defaultGroup = RoutingDefaults.FlatModeSelectionGroup;
            Groups.ReplaceRange(new[] { defaultGroup });
            if (!string.Equals(SelectedGroup, defaultGroup, StringComparison.Ordinal))
            {
                SelectedGroup = defaultGroup;
            }
            return;
        }

        static string? GetGroupNameValue(Dictionary<string, object> group)
        {
            foreach (var entry in group)
            {
                if (entry.Key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value?.ToString();
                }
            }

            return null;
        }

        var groupNames = _proxyCatalogStore.LoadProxyGroups()
            .Select(GetGroupNameValue)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(SelectedGroup) && !groupNames.Contains(SelectedGroup, StringComparer.OrdinalIgnoreCase))
        {
            groupNames.Insert(0, SelectedGroup);
        }

        Groups.ReplaceRange(groupNames);

        if (_isActive)
        {
            _ = LoadGroupsFromCoreAsync();
        }
    }

    private async Task LoadGroupsFromCoreAsync()
    {
        if (!UseSubscriptionPolicyGroups)
        {
            return;
        }

        if (Interlocked.Exchange(ref _groupsLoadInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await _apiClient.GetProxiesAsync().ConfigureAwait(false);
            if (snapshot is null)
            {
                return;
            }

            var groupNames = snapshot.Proxies.Values
                .Where(proxy => proxy.All.Count > 0)
                .Select(proxy => proxy.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(SelectedGroup)
                && !groupNames.Contains(SelectedGroup, StringComparer.OrdinalIgnoreCase))
            {
                groupNames.Insert(0, SelectedGroup);
            }

            if (!_isActive)
            {
                return;
            }

            void Apply()
            {
                if (_isActive)
                {
                    Groups.ReplaceRange(groupNames);
                }
            }

            if (Application.Current is null)
            {
                Apply();
                return;
            }

            try
            {
                await Application.Current.Dispatcher.InvokeAsync(Apply, DispatcherPriority.Background);
            }
            catch
            {
            }
        }
        finally
        {
            Interlocked.Exchange(ref _groupsLoadInProgress, 0);
        }
    }

    private async Task RefreshNodeRowsAsync()
    {
        _rowsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rowsCts = cts;

        var groupName = GetGroupName();
        var cardsPerRow = CardsPerRow;
        var nodes = _allNodes;

        if (cts.IsCancellationRequested)
        {
            return;
        }

        var filtered = await BuildDisplayNodesForGroupAsync(nodes, groupName, cts.Token).ConfigureAwait(false);
        if (cts.IsCancellationRequested || !ReferenceEquals(_rowsCts, cts))
        {
            return;
        }

        var rows = BuildRows(filtered, cardsPerRow);
        void Apply()
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_rowsCts, cts))
            {
                return;
            }

            _currentDisplayNodes = filtered;
            NodeRows.ReplaceRange(rows);
            RebuildDisplayLookup(filtered);
            if (SelectedNode is null || (filtered.Count > 0 && !filtered.Contains(SelectedNode)))
            {
                SelectedNode = filtered.Count > 0 ? filtered[0] : null;
            }
        }

        if (Application.Current is null)
        {
            Apply();
        }
        else
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(Apply, DispatcherPriority.Background);
            }
            catch
            {
            }
        }
    }

    private void RefreshRowsFromCurrent()
    {
        try
        {
            var rows = BuildRows(_currentDisplayNodes, CardsPerRow);
            NodeRows.ReplaceRange(rows);
        }
        catch
        {
        }
    }

    private string GetGroupName()
    {
        if (!UseSubscriptionPolicyGroups)
        {
            return RoutingDefaults.FlatModeSelectionGroup;
        }

        return string.IsNullOrWhiteSpace(SelectedGroup) ? _settingsStore.Settings.SelectionGroup : SelectedGroup;
    }

    private void QueueSelectionGroupSave(string selectionGroup)
    {
        if (!UseSubscriptionPolicyGroups)
        {
            return;
        }

        if (string.Equals(_settingsStore.Settings.SelectionGroup, selectionGroup, StringComparison.Ordinal))
        {
            return;
        }

        _settingsStore.Settings.SelectionGroup = selectionGroup;

        _selectionSaveCts?.Cancel();
        _selectionSaveCts?.Dispose();

        var cts = new CancellationTokenSource();
        _selectionSaveCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    _settingsStore.Save();
                }
            }
            catch
            {
            }
            finally
            {
                if (ReferenceEquals(_selectionSaveCts, cts))
                {
                    _selectionSaveCts = null;
                }

                cts.Dispose();
            }
        });
    }

    private async Task<List<ProxyNode>> BuildDisplayNodesForGroupAsync(
        IReadOnlyList<ProxyNode> nodes,
        string groupName,
        CancellationToken cancellationToken)
    {
        if (!UseSubscriptionPolicyGroups)
        {
            return nodes.ToList();
        }

        if (nodes.Count == 0 || string.IsNullOrWhiteSpace(groupName))
        {
            return nodes.ToList();
        }

        try
        {
            var snapshot = await _apiClient.GetProxiesAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null
                || !snapshot.Proxies.TryGetValue(groupName, out var groupProxy)
                || groupProxy.All.Count == 0)
            {
                return nodes.ToList();
            }

            var result = new List<ProxyNode>(groupProxy.All.Count);
            foreach (var name in groupProxy.All)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (_nodeLookup.TryGetValue(name, out var matches) && matches.Count > 0)
                {
                    result.Add(matches[0]);
                    continue;
                }

                var type = snapshot.Proxies.TryGetValue(name, out var proxy) ? proxy.Type : string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                {
                    type = name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ? "DIRECT"
                        : name.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ? "REJECT"
                        : "ProxyGroup";
                }

                result.Add(new ProxyNode
                {
                    Name = name,
                    Type = type
                });
            }

            return result;
        }
        catch
        {
        }

        return nodes.ToList();
    }

    private List<NodeCardRow> BuildRows(IReadOnlyList<ProxyNode> nodes, int cardsPerRow)
    {
        cardsPerRow = Math.Max(1, cardsPerRow);
        var rows = new List<NodeCardRow>((nodes.Count + cardsPerRow - 1) / cardsPerRow);
        var sourceGroups = BuildSourceGroups(nodes);
        var showHeaders = sourceGroups.Count > 1
                          || sourceGroups.Any(group => !string.IsNullOrWhiteSpace(group.SourceId));

        foreach (var group in sourceGroups)
        {
            if (showHeaders)
            {
                rows.Add(NodeCardRow.Section(GetSourceHeader(group.SourceId, group.Nodes.Count), group.Nodes.Count));
            }

            for (var i = 0; i < group.Nodes.Count; i += cardsPerRow)
            {
                var count = Math.Min(cardsPerRow, group.Nodes.Count - i);
                var items = new List<ProxyNode>(count);
                for (var j = 0; j < count; j++)
                {
                    items.Add(group.Nodes[i + j]);
                }

                rows.Add(NodeCardRow.Cards(items));
            }
        }

        return rows;
    }

    private List<(string SourceId, List<ProxyNode> Nodes)> BuildSourceGroups(IReadOnlyList<ProxyNode> nodes)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<ProxyNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            var key = string.IsNullOrWhiteSpace(node.SourceId) ? string.Empty : node.SourceId;
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<ProxyNode>();
                groups[key] = list;
                order.Add(key);
            }

            list.Add(node);
        }

        return order
            .Select(sourceId => (SourceId: sourceId, Nodes: groups[sourceId]))
            .ToList();
    }

    private string GetSourceHeader(string sourceId, int count)
    {
        var name = string.Empty;
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            _sourceNames.TryGetValue(sourceId, out name);
        }

        name = string.IsNullOrWhiteSpace(name)
            ? string.IsNullOrWhiteSpace(sourceId) ? "Built-in" : sourceId
            : name;

        return $"{name} ({count})";
    }

    private void RebuildNodeLookup(IEnumerable<ProxyNode> nodes)
    {
        _nodeLookup.Clear();
        _activeNodeName = null;
        _activeGroupName = null;

        foreach (var node in nodes)
        {
            if (!_nodeLookup.TryGetValue(node.Name, out var list))
            {
                list = new List<ProxyNode>();
                _nodeLookup[node.Name] = list;
            }

            list.Add(node);
        }
    }

    private void RebuildDisplayLookup(IReadOnlyList<ProxyNode> nodes)
    {
        _displayLookup.Clear();
        foreach (var node in nodes)
        {
            if (!_displayLookup.TryGetValue(node.Name, out var list))
            {
                list = new List<ProxyNode>();
                _displayLookup[node.Name] = list;
            }

            list.Add(node);
        }
    }

    private async Task UpdateActiveNodeAsync()
    {
        if (!_isActive)
        {
            return;
        }

        if (Interlocked.Exchange(ref _activeUpdateInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (Nodes.Count == 0)
            {
                _activeNodeName = null;
                _activeGroupName = null;
                return;
            }

            var groupName = GetGroupName();
            string? activeNode;
            try
            {
                activeNode = await _apiClient.GetSelectedProxyAsync(groupName);
            }
            catch
            {
                return;
            }

            if (string.Equals(_activeGroupName, groupName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_activeNodeName, activeNode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _activeNodeName;
            _activeNodeName = activeNode;
            _activeGroupName = groupName;

            void Apply()
            {
                void SetActive(string? name, bool isActive)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    if (_nodeLookup.TryGetValue(name, out var nodes))
                    {
                        foreach (var node in nodes)
                        {
                            node.IsActive = isActive;
                        }
                    }

                    if (_displayLookup.TryGetValue(name, out var displayed))
                    {
                        foreach (var node in displayed)
                        {
                            node.IsActive = isActive;
                        }
                    }
                }

                SetActive(previous, false);
                SetActive(activeNode, true);
            }

            Dispatch(Apply);
        }
        finally
        {
            Interlocked.Exchange(ref _activeUpdateInProgress, 0);
        }
    }

    public void OnPageActivated()
    {
        if (_isActive)
        {
            return;
        }

        SyncPolicyGroupModeFromSettings();
        LoadGroupsFromCatalog();
        _ = RefreshNodeRowsAsync();

        _isActive = true;
        _activeTimer.Start();
        _ = UpdateActiveNodeAsync();
        _ = LoadGroupsFromCoreAsync();
    }

    public void OnPageDeactivated()
    {
        _isActive = false;
        _activeTimer.Stop();

        try
        {
            _rowsCts?.Cancel();
        }
        catch
        {
        }
    }

    private static void Dispatch(Action action)
    {
        if (Application.Current is null)
        {
            action();
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private async Task TestAllAsync(Func<ProxyNode, Task<int>> test)
    {
        var nodesToTest = _currentDisplayNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Address) && node.Port > 0)
            .ToList();

        if (IsTestingAll || nodesToTest.Count == 0)
        {
            return;
        }

        IsTestingAll = true;
        var semaphore = new SemaphoreSlim(4);
        try
        {
            var tasks = nodesToTest.Select(async node =>
            {
                await semaphore.WaitAsync();
                try
                {
                    int latency;
                    try
                    {
                        latency = await test(node);
                    }
                    catch
                    {
                        latency = -1;
                    }

                    UpdateLatency(node, latency);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            IsTestingAll = false;
        }
    }

    private static void UpdateLatency(ProxyNode node, int latency)
    {
        if (Application.Current is null)
        {
            node.LatencyMs = latency;
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() => node.LatencyMs = latency, DispatcherPriority.Background);
    }

    private void SyncPolicyGroupModeFromSettings()
    {
        var useSubscriptionPolicyGroups = _settingsStore.Settings.UseSubscriptionPolicyGroups;
        if (UseSubscriptionPolicyGroups != useSubscriptionPolicyGroups)
        {
            UseSubscriptionPolicyGroups = useSubscriptionPolicyGroups;
        }
    }

    public ProxyNode? GetActiveNode()
    {
        var active = _currentDisplayNodes.FirstOrDefault(node => node.IsActive);
        if (active is not null)
        {
            return active;
        }

        return _allNodes.FirstOrDefault(node => node.IsActive);
    }
}
