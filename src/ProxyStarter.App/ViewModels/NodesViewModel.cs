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
    private readonly CoreController _coreController;
    private readonly LatencyTestService _latencyTestService;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _activeTimer;
    private bool _isActive;
    private List<ProxyNode> _allNodes = new();
    private IReadOnlyList<ProxyNode> _currentDisplayNodes = Array.Empty<ProxyNode>();
    private Dictionary<string, string> _sourceNames = new(StringComparer.OrdinalIgnoreCase);
    private string? _scopeId;
    private bool _scopeDisabled;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private ProxyNode? _selectedNode;

    [ObservableProperty]
    private bool _isTestingAll;

    [ObservableProperty]
    private bool _isSpeedTestingAll;

    [ObservableProperty]
    private int _cardsPerRow = 2;

    public ObservableRangeCollection<ProxyNode> Nodes { get; } = new();
    public ObservableRangeCollection<NodeCardRow> NodeRows { get; } = new();
    public double SavedScrollOffset { get; set; }

    public NodesViewModel(
        ProxyCatalogStore proxyCatalogStore,
        SubscriptionStore subscriptionStore,
        SubscriptionCacheStore subscriptionCacheStore,
        CoreController coreController,
        LatencyTestService latencyTestService,
        AppSettingsStore settingsStore,
        IDialogService dialogService)
    {
        _proxyCatalogStore = proxyCatalogStore;
        _subscriptionStore = subscriptionStore;
        _subscriptionCacheStore = subscriptionCacheStore;
        _coreController = coreController;
        _latencyTestService = latencyTestService;
        _settingsStore = settingsStore;
        _dialogService = dialogService;

        _activeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _activeTimer.Tick += (_, _) => UpdateActiveNode();
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

    [RelayCommand]
    private void RefreshNodes()
    {
        _ = LoadFromCatalogAsync();
    }

    [RelayCommand]
    private Task SelectNodeAsync() => SelectNodeFromCardAsync(SelectedNode);

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

        try
        {
            await _coreController.SelectNodeAsync(node);
            UpdateActiveNode();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "NodesViewModel: Select node");
            await _dialogService.ShowErrorAsync(DialogError.NodeSelection, ex.Message);
        }
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
            Nodes.ReplaceRange(Array.Empty<ProxyNode>());
            NodeRows.ReplaceRange(Array.Empty<NodeCardRow>());
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
                var profile = profiles.FirstOrDefault(profile => profile.Id == scopeId);
                var disabled = profile is { IsEnabled: false };
                var nodes = scopeId is null ? allNodes
                    : disabled ? _subscriptionCacheStore.LoadNodes(scopeId)
                    : allNodes.Where(node => node.SourceId == scopeId).ToList();
                return (Nodes: nodes.ToList(), SourceNames: names, Disabled: disabled);
            }, cts.Token);

            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            _allNodes = catalog.Nodes;
            _sourceNames = catalog.SourceNames;
            _scopeDisabled = catalog.Disabled;
            _currentDisplayNodes = _allNodes;
            Nodes.ReplaceRange(_allNodes);
            UpdateActiveNode();
            RefreshRowsFromCurrent();
            SelectedNode = _allNodes.FirstOrDefault(node => node.IsActive) ?? _allNodes.FirstOrDefault();
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

    private void UpdateActiveNode()
    {
        var activeName = _coreController.ActiveNodeName;
        var settings = _settingsStore.Settings;
        foreach (var node in _allNodes)
        {
            node.IsActive = !_scopeDisabled && (_coreController.IsRunning
                ? string.Equals(node.Name, activeName, StringComparison.Ordinal)
                : string.Equals(node.SourceId, settings.SelectedNodeSourceId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(RoutingDefaults.OriginalName(node), settings.SelectedNodeName, StringComparison.Ordinal));
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
        _loadCts?.Cancel();
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
