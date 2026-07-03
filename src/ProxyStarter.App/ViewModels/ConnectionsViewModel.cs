using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class ConnectionsViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly ConnectionsMonitorService _monitorService;
    private readonly MihomoApiClient _apiClient;
    private readonly LocalizationService _localizationService;
    private readonly ICollectionView _connectionsView;
    private readonly Dictionary<string, ConnectionItem> _connectionLookup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenConnectionIds = new(StringComparer.Ordinal);
    private MihomoConnectionSnapshot? _latestSnapshot;
    private bool _isActive;
    private int _applyQueued;

    public ObservableCollection<ConnectionItem> Connections { get; } = new();

    public ICollectionView ConnectionsView => _connectionsView;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _uploadTotal = "0 B";

    [ObservableProperty]
    private string _downloadTotal = "0 B";

    [ObservableProperty]
    private string _connectionCount = "0";

    [ObservableProperty]
    private string _pauseLabel = "Pause";

    [ObservableProperty]
    private bool _isPaused;

    public ConnectionsViewModel(
        ConnectionsMonitorService monitorService,
        MihomoApiClient apiClient,
        LocalizationService localizationService)
    {
        _monitorService = monitorService;
        _apiClient = apiClient;
        _localizationService = localizationService;
        _connectionsView = CollectionViewSource.GetDefaultView(Connections);
        _connectionsView.Filter = OnFilterConnection;

        _localizationService.LanguageChanged += (_, _) => UpdatePauseLabel();
        UpdatePauseLabel();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        UpdatePauseLabel();

        if (!IsPaused && _isActive)
        {
            QueueApplySnapshot();
        }
    }

    private void UpdatePauseLabel()
    {
        PauseLabel = IsPaused
            ? _localizationService.GetString("Text_Resume", "Resume")
            : _localizationService.GetString("Text_Pause", "Pause");
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        await _apiClient.CloseAllConnectionsAsync();
    }

    [RelayCommand]
    private async Task CloseConnectionAsync(ConnectionItem? item)
    {
        if (item is null)
        {
            return;
        }

        await _apiClient.CloseConnectionAsync(item.Id);
    }

    partial void OnSearchTextChanged(string value)
    {
        _connectionsView.Refresh();
    }

    private void OnSnapshotUpdated(object? sender, MihomoConnectionSnapshot snapshot)
    {
        _latestSnapshot = snapshot;

        if (!_isActive || IsPaused)
        {
            return;
        }

        QueueApplySnapshot();
    }

    private void ApplySnapshot(MihomoConnectionSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        var items = snapshot.Connections ?? Array.Empty<MihomoConnection>();
        _seenConnectionIds.Clear();

        // Update or add current connections. New items are inserted at the top to keep "fresh" traffic visible.
        foreach (var connection in items)
        {
            var id = connection.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            _seenConnectionIds.Add(id);

            if (!_connectionLookup.TryGetValue(id, out var item))
            {
                item = new ConnectionItem { Id = id };
                _connectionLookup[id] = item;
                Connections.Insert(0, item);
            }

            UpdateConnectionItem(item, connection);
        }

        // Remove connections that are no longer present.
        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            var existing = Connections[i];
            if (string.IsNullOrWhiteSpace(existing.Id) || !_seenConnectionIds.Contains(existing.Id))
            {
                _connectionLookup.Remove(existing.Id);
                Connections.RemoveAt(i);
            }
        }

        ConnectionCount = Connections.Count.ToString(CultureInfo.InvariantCulture);
        UploadTotal = FormatBytes(snapshot.UploadTotal);
        DownloadTotal = FormatBytes(snapshot.DownloadTotal);
    }

    private bool OnFilterConnection(object? item)
    {
        if (item is not ConnectionItem connection)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return connection.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
               || connection.Rule.Contains(query, StringComparison.OrdinalIgnoreCase)
               || connection.Proxy.Contains(query, StringComparison.OrdinalIgnoreCase)
               || connection.Process.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateConnectionItem(ConnectionItem item, MihomoConnection connection)
    {
        var proxy = connection.Proxy;
        if (string.IsNullOrWhiteSpace(proxy) && connection.Chains.Count > 0)
        {
            proxy = connection.Chains.Last();
        }

        item.Target = string.IsNullOrWhiteSpace(connection.Target) ? "Unknown" : connection.Target;
        item.Network = connection.Network.ToUpperInvariant();
        item.Type = string.IsNullOrWhiteSpace(connection.Type) ? string.Empty : connection.Type.ToUpperInvariant();
        item.Rule = string.IsNullOrWhiteSpace(connection.Rule) ? "MATCH" : connection.Rule;
        item.Proxy = proxy ?? string.Empty;
        item.Process = connection.Process;
        item.Upload = connection.Upload;
        item.Download = connection.Download;
        item.Start = connection.Start;
        item.Age = FormatAge(DateTimeOffset.Now - connection.Start);
    }

    private void QueueApplySnapshot()
    {
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1)
        {
            return;
        }

        if (Application.Current is null)
        {
            Interlocked.Exchange(ref _applyQueued, 0);
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_isActive || IsPaused)
                {
                    return;
                }

                var snapshot = _latestSnapshot;
                if (snapshot is not null)
                {
                    ApplySnapshot(snapshot);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _applyQueued, 0);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void OnPageActivated()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;

        try
        {
            _monitorService.SnapshotUpdated -= OnSnapshotUpdated;
            _monitorService.SnapshotUpdated += OnSnapshotUpdated;
            _monitorService.Start();
        }
        catch
        {
        }

        if (!IsPaused)
        {
            QueueApplySnapshot();
        }
    }

    public void OnPageDeactivated()
    {
        _isActive = false;

        try
        {
            _monitorService.SnapshotUpdated -= OnSnapshotUpdated;
            _monitorService.Stop();
        }
        catch
        {
        }
    }

    private static string FormatAge(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 10)
        {
            return "Just now";
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{elapsed.Seconds}s ago";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{elapsed.Minutes}m ago";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        return $"{(int)elapsed.TotalDays}d ago";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[index];
    }
}
