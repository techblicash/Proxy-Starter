using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class NodesPage : Page
{
    private const double CardMinWidth = 360;
    private const double CardGap = 12;
    private const int MaxCardsPerRow = 4;
    private readonly DispatcherTimer _resizeTimer;
    private ProxyNode? _locatedNode;

    public NodesPage(NodesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;

        _resizeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            UpdateCardsPerRowSafe();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is NodesViewModel viewModel)
        {
            viewModel.NodeRows.CollectionChanged += OnNodeRowsChanged;
        }

        (DataContext as IPageLifecycleAware)?.OnPageActivated();
        UpdateCardsPerRowSafe();
        RestoreScrollOffsetSafe();
    }

    private void OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _resizeTimer.Stop();
        if (DataContext is NodesViewModel viewModel)
        {
            viewModel.NodeRows.CollectionChanged -= OnNodeRowsChanged;
        }

        ClearSearchResult();
        SaveScrollOffsetSafe();
        (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }

    private void OnLocateActiveNodeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NodesViewModel viewModel)
        {
            return;
        }

        var activeNode = viewModel.GetActiveNode();
        if (activeNode is null)
        {
            return;
        }

        ClearSearchResult();
        LocateNode(viewModel, activeNode);
    }

    private void OnNodeSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchResult();
    }

    private void OnNodeRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_locatedNode is null || DataContext is NodesViewModel viewModel
            && !viewModel.NodeRows.Any(row => row.Items.Contains(_locatedNode)))
        {
            ClearSearchResult();
        }
    }

    private void OnNodeSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNextNode();
            e.Handled = true;
        }
    }

    private void OnFindNodeClick(object sender, RoutedEventArgs e)
    {
        FindNextNode();
    }

    private void FindNextNode()
    {
        if (DataContext is not NodesViewModel viewModel)
        {
            return;
        }

        var query = NodeSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            NodeSearchBox.Focus();
            return;
        }

        var matches = viewModel.NodeRows.SelectMany(row => row.Items)
            .Where(node => node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || node.Address.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var next = (_locatedNode is null ? -1 : matches.IndexOf(_locatedNode)) + 1;
        ClearSearchResult();
        if (matches.Count == 0)
        {
            NodeSearchStatus.SetResourceReference(TextBlock.TextProperty, "Text_NoMatchingNodes");
            return;
        }

        next %= matches.Count;
        _locatedNode = matches[next];
        _locatedNode.IsLocated = true;
        NodeSearchStatus.Text = $"{next + 1} / {matches.Count}";
        LocateNode(viewModel, _locatedNode);
    }

    private void ClearSearchResult()
    {
        if (_locatedNode is not null)
        {
            _locatedNode.IsLocated = false;
            _locatedNode = null;
        }

        if (NodeSearchStatus is not null)
        {
            NodeSearchStatus.Text = string.Empty;
        }
    }

    private void LocateNode(NodesViewModel viewModel, ProxyNode node)
    {
        var row = viewModel.NodeRows.FirstOrDefault(cardRow => cardRow.Items.Contains(node));
        if (row is null)
        {
            return;
        }

        viewModel.SelectedNode = node;
        NodesList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() => NodesList.ScrollIntoView(row), DispatcherPriority.Background);
    }

    private void UpdateCardsPerRowSafe()
    {
        try
        {
            if (DataContext is not NodesViewModel viewModel)
            {
                return;
            }

            var width = NodesListHost?.ActualWidth ?? ActualWidth;
            width = Math.Max(0, width - 12); // Border padding: 6 * 2
            if (width <= 0)
            {
                return;
            }

            var cards = (int)Math.Floor((width + CardGap) / (CardMinWidth + CardGap));
            cards = Math.Clamp(cards, 1, MaxCardsPerRow);
            viewModel.CardsPerRow = cards;
        }
        catch
        {
        }
    }

    private void SaveScrollOffsetSafe()
    {
        try
        {
            if (DataContext is not NodesViewModel viewModel)
            {
                return;
            }

            var scrollViewer = FindDescendant<ScrollViewer>(NodesList);
            if (scrollViewer is null)
            {
                return;
            }

            viewModel.SavedScrollOffset = scrollViewer.VerticalOffset;
        }
        catch
        {
        }
    }

    private void RestoreScrollOffsetSafe()
    {
        try
        {
            if (DataContext is not NodesViewModel viewModel)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var scrollViewer = FindDescendant<ScrollViewer>(NodesList);
                    if (scrollViewer is null)
                    {
                        return;
                    }

                    scrollViewer.ScrollToVerticalOffset(viewModel.SavedScrollOffset);
                }
                catch
                {
                }
            }, DispatcherPriority.Loaded);
        }
        catch
        {
        }
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T typed)
        {
            return typed;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindDescendant<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
