using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

    private readonly List<ProxyNode> _allNodes = new();
    private readonly ObservableCollection<ProxyNode> _filteredNodes = new();
    private bool _suppressSelection;

    public TrayMenuWindow(MainWindowViewModel viewModel, MihomoApiClient apiClient, AppSettingsStore settingsStore)
    {
        _viewModel = viewModel;
        _apiClient = apiClient;
        _settingsStore = settingsStore;
        InitializeComponent();
        DataContext = _viewModel;

        NodesList.ItemsSource = _filteredNodes;

        Loaded += async (_, _) =>
        {
            ApplicationThemeManager.Apply(this);
            await LoadNodesAsync();
        };
        Deactivated += (_, _) => Close();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
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
        _viewModel.ToggleCoreCommand.Execute(null);
        Close();
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ExitCommand.Execute(null);
        Close();
    }

    private async Task LoadNodesAsync()
    {
        NodesStatusText.Text = string.Empty;
        _allNodes.Clear();
        _filteredNodes.Clear();

        var groupName = _settingsStore.Settings.SelectionGroup;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        try
        {
            var snapshot = await _apiClient.GetProxiesAsync();
            if (snapshot is null
                || !snapshot.Proxies.TryGetValue(groupName, out var group)
                || group.All.Count == 0)
            {
                NodesStatusText.Text = GetString("Text_Stopped", "Stopped");
                return;
            }

            var active = group.Now;
            foreach (var name in group.All.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                _allNodes.Add(new ProxyNode
                {
                    Name = name,
                    IsActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase)
                });
            }

            ApplyFilter(NodeSearchBox.Text);

            NodesStatusText.Text = $"{groupName} · {_allNodes.Count}";
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

        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var node in _allNodes)
            {
                _filteredNodes.Add(node);
            }

            return;
        }

        foreach (var node in _allNodes.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
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

        var groupName = _settingsStore.Settings.SelectionGroup;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            Close();
            return;
        }

        _suppressSelection = true;
        try
        {
            var success = await _apiClient.SetProxySelectionAsync(groupName, node.Name);
            if (!success)
            {
                return;
            }

            foreach (var item in _allNodes)
            {
                item.IsActive = string.Equals(item.Name, node.Name, StringComparison.OrdinalIgnoreCase);
            }
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
