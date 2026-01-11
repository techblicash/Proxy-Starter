using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class NodesPage : Page
{
    private const double CardMinWidth = 360;
    private const double CardGap = 12;
    private const int MaxCardsPerRow = 4;
    private readonly DispatcherTimer _resizeTimer;

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
        SaveScrollOffsetSafe();
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
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
