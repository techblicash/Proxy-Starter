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
    private readonly SubscriptionCacheStore _subscriptionCacheStore;
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
    private IReadOnlyList<Dictionary<string, object>> _catalogGroups = Array.Empty<Dictionary<string, object>>();
    private string? _scopeId;
    private string _flatGroup = RoutingDefaults.FlatModeSelectionGroup;
    private bool _scopeDisabled;
    private bool _loadingGroups;
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
    private bool _isSpeedTestingAll;

    [ObservableProperty]
    private string _selectedGroup;

    [ObservableProperty]
    private int _cardsPerRow = 2;

    public ObservableRangeCollection<ProxyNode> Nodes { get; } = new();
    public ObservableRangeCollection<NodeCardRow> NodeRows { get; } = new();
    public ObservableRangeCollection<string> Groups { get; } = new();
    public bool ShowSelectionGroup => Groups.Count > 0;

    public double SavedScrollOffset { get; set; }

    public NodesViewModel(
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionStore subscriptionStore,
        SubscriptionCacheStore subscriptionCacheStore,
        MihomoApiClient apiClient,
        LatencyTestService latencyTestService,
        AppSettingsStore settingsStore,
        IDialogService dialogService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _subscriptionStore = subscriptionStore;
        _subscriptionCacheStore = subscriptionCacheStore;
        _apiClient = apiClient;
        _latencyTestService = latencyTestService;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _selectedGroup = settingsStore.Settings.SelectionGroup;

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

    partial void OnSelectedGroupChanged(string value)
    {
        if (_loadingGroups || string.IsNullOrWhiteSpace(value))
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
        if (SelectedNode is null || _scopeDisabled)
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
                await _dialogService.ShowErrorAsync(DialogError.NodeSelection);
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
    private async Task SpeedTestAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        await SpeedTestNodeAsync(SelectedNode);
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
        if (node is null || _scopeDisabled)
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
                await _dialogService.ShowErrorAsync(DialogError.NodeSelection);
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
    private async Task SpeedTestNodeAsync(ProxyNode? node)
    {
        if (node is null)
        {
            return;
        }

        UpdateSpeed(node, "Testing...");
        double speed;
        try
        {
            speed = await _latencyTestService.TestProxyDownloadSpeedMegabytesPerSecondAsync(node.Name);
        }
        catch
        {
            speed = -1;
        }

        UpdateSpeed(node, speed > 0 ? $"{speed:0.##} MB/s" : "Failed");
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

    [RelayCommand]
    private async Task SpeedTestAllAsync()
    {
        var nodesToTest = _currentDisplayNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Name)
                           && !node.Name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                           && !node.Name.Equals("REJECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (IsSpeedTestingAll || nodesToTest.Count == 0)
        {
            return;
        }

        IsSpeedTestingAll = true;
        var semaphore = new SemaphoreSlim(2);
        try
        {
            var tasks = nodesToTest.Select(async node =>
            {
                await semaphore.WaitAsync();
                try
                {
                    UpdateSpeed(node, "Testing...");
                    double speed;
                    try
                    {
                        speed = await _latencyTestService.TestProxyDownloadSpeedMegabytesPerSecondAsync(node.Name);
                    }
                    catch
                    {
                        speed = -1;
                    }

                    UpdateSpeed(node, speed > 0 ? $"{speed:0.##} MB/s" : "Failed");
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
            IsSpeedTestingAll = false;
        }
    }

    private async Task LoadFromCatalogAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        var scopeId = _subscriptionStore.SelectedProfileId;
        if (!string.Equals(_scopeId, scopeId, StringComparison.OrdinalIgnoreCase))
        {
            _rowsCts?.Cancel();
            Nodes.ReplaceRange(Array.Empty<ProxyNode>());
            NodeRows.ReplaceRange(Array.Empty<NodeCardRow>());
            Groups.ReplaceRange(Array.Empty<string>());
            OnPropertyChanged(nameof(ShowSelectionGroup));
            SelectedNode = null;
            _currentDisplayNodes = Array.Empty<ProxyNode>();
            _scopeId = scopeId;
        }

        try
        {
            var catalog = await Task.Run(() =>
            {
                var profiles = _subscriptionStore.Load();
                var names = profiles.GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
                var allNodes = _proxyCatalogStore.LoadNodes();
                var allGroups = _proxyCatalogStore.LoadProxyGroups();
                var profile = profiles.FirstOrDefault(profile => profile.Id == scopeId);
                var disabled = profile is { IsEnabled: false };
                var nodes = scopeId is null ? allNodes
                    : disabled ? _subscriptionCacheStore.LoadNodes(scopeId)
                    : allNodes.Where(node => node.SourceId == scopeId).ToList();
                var groups = scopeId is null ? allGroups
                    : disabled ? _subscriptionCacheStore.LoadGroups(scopeId)
                    : allGroups.Where(group => RoutingDefaults.GroupSource(group) == scopeId).ToList();

                return (Nodes: nodes.ToList(), Groups: groups, SourceNames: names, Disabled: disabled,
                    FlatGroup: RoutingDefaults.GetFlatGroupName(allNodes, allGroups));
            }, cts.Token);

            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            _allNodes = catalog.Nodes;
            _catalogGroups = catalog.Groups;
            _sourceNames = catalog.SourceNames;
            _flatGroup = catalog.FlatGroup;
            _scopeDisabled = catalog.Disabled;
            _activeNodeName = null;
            _activeGroupName = null;
            RebuildNodeLookup(_allNodes);
            Nodes.ReplaceRange(_allNodes);
            LoadGroupsFromCatalog();
            await RefreshNodeRowsAsync();
            await UpdateActiveNodeAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }
            cts.Dispose();
        }
    }

    private void LoadGroupsFromCatalog()
    {
        var groupNames = _catalogGroups.Select(RoutingDefaults.GroupName)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var preferredGroup = groupNames.FirstOrDefault(name =>
            name.Equals(_settingsStore.Settings.SelectionGroup, StringComparison.OrdinalIgnoreCase));

        _loadingGroups = true;
        try
        {
            Groups.ReplaceRange(groupNames);
            SelectedGroup = preferredGroup ?? groupNames.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(ShowSelectionGroup));
        }
        finally
        {
            _loadingGroups = false;
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
            if (SelectedNode is null || !filtered.Contains(SelectedNode))
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

    private string GetGroupName() => string.IsNullOrWhiteSpace(SelectedGroup) ? _flatGroup : SelectedGroup;

    private void QueueSelectionGroupSave(string selectionGroup)
    {
        if (_scopeDisabled)
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
        var group = _catalogGroups.FirstOrDefault(group => RoutingDefaults.GroupName(group) == groupName);
        if (group is null)
        {
            return nodes.ToList();
        }

        var configuredMembers = RoutingDefaults.Names(group, "proxies").ToList();
        var groups = _catalogGroups.Select(RoutingDefaults.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = configuredMembers;
        if (!_scopeDisabled)
        {
            var snapshot = await _apiClient.GetProxiesAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot?.Proxies.TryGetValue(groupName, out var runningGroup) == true && runningGroup.All.Count > 0)
            {
                members = runningGroup.All.ToList();
            }
        }

        var lookup = nodes.ToLookup(node => node.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProxyNode>(members.Count);
        foreach (var name in members.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var node = lookup[name].FirstOrDefault();
            if (node is not null)
            {
                result.Add(node);
            }
            else if (groups.Contains(name) || (configuredMembers.Contains(name, StringComparer.OrdinalIgnoreCase)
                     && name is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE"))
            {
                result.Add(new ProxyNode { Name = name, Type = groups.Contains(name) ? "ProxyGroup" : name });
            }
        }

        return result;
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
        if (!_isActive || _scopeDisabled)
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

        _isActive = true;
        _activeTimer.Start();
        _ = LoadFromCatalogAsync();
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

    private static void UpdateSpeed(ProxyNode node, string speedText)
    {
        if (Application.Current is null)
        {
            node.SpeedText = speedText;
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() => node.SpeedText = speedText, DispatcherPriority.Background);
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
