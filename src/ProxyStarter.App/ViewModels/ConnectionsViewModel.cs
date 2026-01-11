using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class ConnectionsViewModel : ObservableObject
{
    private readonly ConnectionsMonitorService _monitorService;
    private readonly MihomoApiClient _apiClient;
    private readonly LocalizationService _localizationService;
    private readonly ICollectionView _connectionsView;

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

        _monitorService.SnapshotUpdated += OnSnapshotUpdated;
        _monitorService.Start();
        _localizationService.LanguageChanged += (_, _) => UpdatePauseLabel();
        UpdatePauseLabel();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        UpdatePauseLabel();
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
        if (IsPaused)
        {
            return;
        }

        if (Application.Current is null)
        {
            ApplySnapshot(snapshot);
            return;
        }

        Application.Current.Dispatcher.Invoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(MihomoConnectionSnapshot snapshot)
    {
        var items = snapshot.Connections ?? Array.Empty<MihomoConnection>();
        Connections.Clear();

        foreach (var connection in items)
        {
            Connections.Add(MapConnection(connection));
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

    private static ConnectionItem MapConnection(MihomoConnection connection)
    {
        var proxy = connection.Proxy;
        if (string.IsNullOrWhiteSpace(proxy) && connection.Chains.Count > 0)
        {
            proxy = connection.Chains.Last();
        }

        var item = new ConnectionItem
        {
            Id = connection.Id,
            Target = string.IsNullOrWhiteSpace(connection.Target) ? "Unknown" : connection.Target,
            Network = connection.Network.ToUpperInvariant(),
            Type = string.IsNullOrWhiteSpace(connection.Type) ? string.Empty : connection.Type.ToUpperInvariant(),
            Rule = string.IsNullOrWhiteSpace(connection.Rule) ? "MATCH" : connection.Rule,
            Proxy = proxy ?? string.Empty,
            Process = connection.Process,
            Upload = connection.Upload,
            Download = connection.Download,
            Start = connection.Start,
            Age = FormatAge(DateTimeOffset.Now - connection.Start)
        };

        return item;
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
