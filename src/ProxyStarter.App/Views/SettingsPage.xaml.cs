using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class SettingsPage : Page
{
    private double _savedScrollOffset;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        (DataContext as IPageLifecycleAware)?.OnPageActivated();
        RestoreScrollOffsetSafe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveScrollOffsetSafe();
        (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }

    private void SaveScrollOffsetSafe()
    {
        try
        {
            var scrollViewer = FindDescendant<ScrollViewer>(SettingsList);
            if (scrollViewer is null)
            {
                return;
            }

            _savedScrollOffset = scrollViewer.VerticalOffset;
        }
        catch
        {
        }
    }

    private void RestoreScrollOffsetSafe()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var scrollViewer = FindDescendant<ScrollViewer>(SettingsList);
                    if (scrollViewer is null)
                    {
                        return;
                    }

                    scrollViewer.ScrollToVerticalOffset(_savedScrollOffset);
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
