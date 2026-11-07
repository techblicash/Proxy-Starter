using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly CoreController _coreController;
    private readonly IDialogService _dialogService;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCatalogStore _proxyCatalogStore;

    private readonly List<ProxyNode> _allNodes = new();
    private readonly ObservableCollection<ProxyNode> _filteredNodes = new();

    private bool _suppressSelection;

    public TrayMenuWindow(
        MainWindowViewModel viewModel,
        CoreController coreController,
        IDialogService dialogService,
        AppSettingsStore settingsStore,
        ProxyCatalogStore proxyCatalogStore)
    {
        _viewModel = viewModel;
        _coreController = coreController;
        _dialogService = dialogService;
        _settingsStore = settingsStore;
        _proxyCatalogStore = proxyCatalogStore;

        InitializeComponent();
        DataContext = _viewModel;
        NodesList.ItemsSource = _filteredNodes;

        Loaded += (_, _) =>
        {
            ApplicationThemeManager.Apply(this);
            LoadNodes();
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

    private void OnRefreshNodesClicked(object sender, RoutedEventArgs e)
    {
        LoadNodes();
    }

    private void LoadNodes()
    {
        var nodes = _proxyCatalogStore.LoadNodes();
        var activeName = _coreController.IsRunning
            ? _coreController.ActiveNodeName
            : RoutingDefaults.FindSelectedNode(_settingsStore.Settings, nodes)?.Name;
        _allNodes.Clear();
        foreach (var node in nodes)
        {
            node.IsActive = string.Equals(node.Name, activeName, StringComparison.Ordinal);
            _allNodes.Add(node);
        }

        ApplyFilter(NodeSearchBox.Text);
        NodesStatusText.Text = $"{_allNodes.Count}";
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

        _suppressSelection = true;
        try
        {
            await _coreController.SelectNodeAsync(node);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "TrayMenuWindow: Select node");
            await _dialogService.ShowErrorAsync(DialogError.NodeSelection, ex.Message);
        }
        finally
        {
            _suppressSelection = false;
            Close();
        }
    }

}
