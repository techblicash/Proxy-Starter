using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Services;
using ProxyStarter.App.ViewModels;
using Forms = System.Windows.Forms;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace ProxyStarter.App;

public partial class MainWindow : FluentWindow
{
    private const double WindowDragRegionHeight = 32.0;
    private const int PaneWidthAnimationDurationMs = 180;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private readonly MainWindowViewModel _viewModel;
    private readonly INavigationService _navigationService;
    private readonly LocalizationService _localizationService;

    private Forms.NotifyIcon? _notifyIcon;
    private Views.TrayMenuWindow? _trayMenuWindow;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider,
        LocalizationService localizationService)
    {
        _viewModel = viewModel;
        _navigationService = navigationService;
        _localizationService = localizationService;

        InitializeComponent();
        DataContext = _viewModel;

        RootNavigation.SetPageProviderService(pageProvider);
        RootNavigation.SetServiceProvider(App.Services);
        _navigationService.SetNavigationControl(RootNavigation);

        InitializePaneBackdropSync();
        InitializeTrayIcon();
        InitializeLocalization();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;

        UpdateMaximizeButtonType();
    }

    private void InitializeLocalization()
    {
        _localizationService.LanguageChanged += (_, _) => Dispatch(RefreshNavigationText);
        RefreshNavigationText();
    }

    private void RefreshNavigationText()
    {
        var appName = _localizationService.GetString("Text_AppName", "Proxy Starter");
        Title = appName;
        RootNavigation.PaneTitle = appName;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = appName;
        }

        if (NavDashboard is not null)
        {
            NavDashboard.Content = _localizationService.GetString("Text_Dashboard", "Dashboard");
        }

        if (NavProfiles is not null)
        {
            NavProfiles.Content = _localizationService.GetString("Text_Profiles", "Profiles");
        }

        if (NavNodes is not null)
        {
            NavNodes.Content = _localizationService.GetString("Text_Nodes", "Nodes");
        }

        if (NavRules is not null)
        {
            NavRules.Content = _localizationService.GetString("Text_Rules", "Rules");
        }

        if (NavConnections is not null)
        {
            NavConnections.Content = _localizationService.GetString("Text_Connections", "Connections");
        }

        if (NavLogs is not null)
        {
            NavLogs.Content = _localizationService.GetString("Text_Logs", "Logs");
        }

        if (NavSettings is not null)
        {
            NavSettings.Content = _localizationService.GetString("Text_Settings", "Settings");
        }
    }

    private void OnMinimizeButtonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemCommands.MinimizeWindow(this);
        }
        catch
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeButtonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }
        }
        catch
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        UpdateMaximizeButtonType();
    }

    private void OnCloseButtonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemCommands.CloseWindow(this);
        }
        catch
        {
            Close();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonType();
    }

    private void UpdateMaximizeButtonType()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        MaximizeButton.ButtonType = WindowState == WindowState.Maximized
            ? TitleBarButtonType.Restore
            : TitleBarButtonType.Maximize;
    }

    private void InitializePaneBackdropSync()
    {
        SyncPaneBackdropWidth(animated: false);

        RootNavigation.Loaded += (_, _) => SyncPaneBackdropWidth(animated: false);

        try
        {
            DependencyPropertyDescriptor
                .FromProperty(NavigationView.IsPaneOpenProperty, typeof(NavigationView))
                .AddValueChanged(RootNavigation, (_, _) =>
                    Dispatcher.BeginInvoke(() => SyncPaneBackdropWidth(animated: true), DispatcherPriority.Loaded));
        }
        catch
        {
        }
    }

    private void SyncPaneBackdropWidth(bool animated)
    {
        var targetWidth = GetPaneBackdropTargetWidth();

        if (!animated)
        {
            PaneBackdrop.BeginAnimation(WidthProperty, null);
            PaneBackdrop.Width = targetWidth;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(PaneWidthAnimationDurationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        animation.Completed += (_, _) =>
        {
            PaneBackdrop.BeginAnimation(WidthProperty, null);
            PaneBackdrop.Width = targetWidth;
        };

        PaneBackdrop.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private double GetPaneBackdropTargetWidth()
    {
        var width = RootNavigation.IsPaneOpen ? RootNavigation.OpenPaneLength : RootNavigation.CompactPaneLength;
        if (double.IsNaN(width) || double.IsInfinity(width) || width < 0)
        {
            width = RootNavigation.IsPaneOpen ? 240 : 48;
        }

        return width;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.Y > WindowDragRegionHeight)
        {
            return;
        }

        var originalSource = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(originalSource, WindowButtons) || IsInteractiveElement(originalSource))
        {
            return;
        }

        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginSystemDragMove();
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.Y > WindowDragRegionHeight)
        {
            return;
        }

        var originalSource = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(originalSource, WindowButtons) || IsInteractiveElement(originalSource))
        {
            return;
        }

        e.Handled = true;

        try
        {
            var cursor = Forms.Cursor.Position;
            var scale = DpiHelper.GetScaleForPoint(cursor.X, cursor.Y);
            var screenPoint = new System.Windows.Point(cursor.X / scale, cursor.Y / scale);
            SystemCommands.ShowSystemMenu(this, screenPoint);
        }
        catch
        {
        }
    }

    private void BeginSystemDragMove()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                DragMove();
                return;
            }

            ReleaseCapture();
            SendMessage(handle, WmNcLeftButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
        catch
        {
            try
            {
                DragMove();
            }
            catch
            {
            }
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element is null || ancestor is null)
        {
            return false;
        }

        var current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.RepeatButton
                or System.Windows.Controls.Primitives.Selector
                or System.Windows.Documents.Hyperlink)
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        return element switch
        {
            Visual => VisualTreeHelper.GetParent(element),
            System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element),
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            _ => null
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _navigationService.Navigate(typeof(Views.DashboardPage));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsExitRequested)
        {
            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Proxy Starter",
            Icon = LoadTrayIcon(),
            Visible = true,
        };

        _notifyIcon.MouseUp += (_, e) =>
        {
            switch (e.Button)
            {
                case Forms.MouseButtons.Left:
                    Dispatch(() => _viewModel.ShowWindowCommand.Execute(null));
                    break;
                case Forms.MouseButtons.Right:
                    Dispatch(ShowTrayMenu);
                    break;
            }
        };

        if (Application.Current is not null)
        {
            Application.Current.Exit += (_, _) => DisposeTrayIcon();
        }
    }

    private void ShowTrayMenu()
    {
        CloseTrayMenu();

        if (_notifyIcon is null)
        {
            return;
        }

        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var scale = DpiHelper.GetScaleForPoint(cursor.X, cursor.Y);

        var workingArea = screen.WorkingArea;
        var workingLeft = workingArea.Left / scale;
        var workingTop = workingArea.Top / scale;
        var workingRight = workingArea.Right / scale;
        var workingBottom = workingArea.Bottom / scale;

        var cursorX = cursor.X / scale;
        var cursorY = cursor.Y / scale;

        var menuWindow = new Views.TrayMenuWindow(
            _viewModel,
            App.Services.GetRequiredService<MihomoApiClient>(),
            App.Services.GetRequiredService<AppSettingsStore>())
        {
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        _trayMenuWindow = menuWindow;
        menuWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenuWindow, menuWindow))
            {
                _trayMenuWindow = null;
            }
        };

        var width = menuWindow.Width;
        var height = menuWindow.Height;

        var left = cursorX;
        var top = cursorY;

        if (left + width > workingRight)
        {
            left = workingRight - width;
        }

        if (top + height > workingBottom)
        {
            top = workingBottom - height;
        }

        if (left < workingLeft)
        {
            left = workingLeft;
        }

        if (top < workingTop)
        {
            top = workingTop;
        }

        menuWindow.Left = left;
        menuWindow.Top = top;
        menuWindow.Show();
        menuWindow.Activate();
    }

    private void CloseTrayMenu()
    {
        try
        {
            _trayMenuWindow?.Close();
        }
        catch
        {
        }
        finally
        {
            _trayMenuWindow = null;
        }
    }

    private void DisposeTrayIcon()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        CloseTrayMenu();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.png", UriKind.Absolute));
            if (streamInfo?.Stream is null)
            {
                return SystemIcons.Application;
            }

            using var bitmap = new Bitmap(streamInfo.Stream);
            var handle = bitmap.GetHicon();
            try
            {
                return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.Invoke(action);
    }
}
