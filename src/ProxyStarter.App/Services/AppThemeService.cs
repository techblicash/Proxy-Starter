using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ProxyStarter.App.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ProxyStarter.App.Services;

public sealed class AppThemeService
{
    private static bool _isFollowingSystem;
    private static bool _systemThemeTrackingEnabled;
    private static readonly UserPreferenceChangedEventHandler SystemThemeChangedHandler = OnSystemThemeChanged;

    public void ApplyTheme(string? theme, Window? window = null)
    {
        var normalizedTheme = (theme ?? string.Empty).Trim();

        var targetWindow = window ?? Application.Current?.MainWindow;
        if (targetWindow is null)
        {
            return;
        }

        var isSystem = string.Equals(normalizedTheme, "System", StringComparison.OrdinalIgnoreCase);

        _isFollowingSystem = isSystem;
        if (isSystem)
        {
            EnableSystemThemeTracking();
        }
        else
        {
            DisableSystemThemeTracking();
        }

        var desiredTheme = isSystem
            ? GetSystemApplicationTheme()
            : string.Equals(normalizedTheme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;

        ApplyThemeResources(desiredTheme);
        UpdateAcrylicTints(desiredTheme);
        UpdateFontSize();
        EnsureWindowBackdrop(targetWindow);

        ApplyToWindowWhenReady(targetWindow, desiredTheme);
    }

    private static ApplicationTheme GetSystemApplicationTheme()
    {
        try
        {
            if (ApplicationThemeManager.IsSystemHighContrast())
            {
                return ApplicationTheme.HighContrast;
            }
        }
        catch
        {
        }

        return GetAppsThemeFromRegistry();
    }

    private static ApplicationTheme GetAppsThemeFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int useLightTheme && useLightTheme == 0)
            {
                return ApplicationTheme.Dark;
            }
        }
        catch
        {
        }

        return ApplicationTheme.Light;
    }

    private static void ApplyThemeResources(ApplicationTheme desiredTheme)
    {
        var currentTheme = ApplicationThemeManager.GetAppTheme();
        if (currentTheme == desiredTheme)
        {
            return;
        }

        ApplicationThemeManager.Apply(desiredTheme, WindowBackdropType.Acrylic, true);
    }

    private static void ApplyToWindowWhenReady(Window window, ApplicationTheme desiredTheme)
    {
        if (window.IsLoaded)
        {
            EnsureWindowBackdrop(window);
            ApplicationThemeManager.Apply(window);
            EnsureWindowBackdrop(window);
            TryUpdateWindowBackground(window, desiredTheme);
            return;
        }

        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            window.Loaded -= handler;
            EnsureWindowBackdrop(window);
            ApplicationThemeManager.Apply(window);
            EnsureWindowBackdrop(window);
            TryUpdateWindowBackground(window, desiredTheme);
        };

        window.Loaded += handler;
    }

    private static void EnsureWindowBackdrop(Window window)
    {
        if (window is FluentWindow fluentWindow && fluentWindow.WindowBackdropType != WindowBackdropType.Acrylic)
        {
            fluentWindow.WindowBackdropType = WindowBackdropType.Acrylic;
        }
    }

    private static void UpdateAcrylicTints(ApplicationTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var isDark = theme == ApplicationTheme.Dark || theme == ApplicationTheme.HighContrast;

        AppSettings? settings = null;
        try
        {
            settings = global::ProxyStarter.App.App.Services.GetService<AppSettingsStore>()?.Settings;
        }
        catch
        {
        }

        var paneAlpha = System.Math.Clamp(settings?.PaneAcrylicOpacity ?? (isDark ? 0x70 : 0x66), 0, 255);
        var contentAlpha = System.Math.Clamp(settings?.ContentAcrylicOpacity ?? (isDark ? 0xA8 : 0xCC), 0, 255);

        var paneTint = isDark
            ? System.Windows.Media.Color.FromArgb((byte)paneAlpha, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb((byte)paneAlpha, 0xFF, 0xFF, 0xFF);

        var contentTint = isDark
            ? System.Windows.Media.Color.FromArgb((byte)contentAlpha, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb((byte)contentAlpha, 0xFF, 0xFF, 0xFF);

        resources["PaneAcrylicTintBrush"] = new SolidColorBrush(paneTint);
        resources["ContentAcrylicTintBrush"] = new SolidColorBrush(contentTint);
    }

    private static void UpdateFontSize()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        AppSettings? settings = null;
        try
        {
            settings = global::ProxyStarter.App.App.Services.GetService<AppSettingsStore>()?.Settings;
        }
        catch
        {
        }

        var fontSize = System.Math.Clamp(settings?.FontSize ?? 12, 10, 24);
        resources["AppFontSize"] = (double)fontSize;
    }

    private static void TryUpdateWindowBackground(Window window, ApplicationTheme desiredTheme)
    {
        try
        {
            WindowBackgroundManager.UpdateBackground(window, desiredTheme, WindowBackdropType.Acrylic);
        }
        catch
        {
        }
    }

    private static void EnableSystemThemeTracking()
    {
        if (_systemThemeTrackingEnabled)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += SystemThemeChangedHandler;
        _systemThemeTrackingEnabled = true;
    }

    private static void DisableSystemThemeTracking()
    {
        if (!_systemThemeTrackingEnabled)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemThemeChangedHandler;
        _systemThemeTrackingEnabled = false;
    }

    private static void OnSystemThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!_isFollowingSystem)
        {
            return;
        }

        if (e.Category is not UserPreferenceCategory.General
            and not UserPreferenceCategory.VisualStyle
            and not UserPreferenceCategory.Color)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        void ApplyNow()
        {
            if (Application.Current?.MainWindow is not Window window)
            {
                return;
            }

            var desiredTheme = GetSystemApplicationTheme();
            ApplyThemeResources(desiredTheme);
            UpdateAcrylicTints(desiredTheme);
            EnsureWindowBackdrop(window);
            ApplicationThemeManager.Apply(window);
            EnsureWindowBackdrop(window);
            TryUpdateWindowBackground(window, desiredTheme);
        }

        if (dispatcher.CheckAccess())
        {
            ApplyNow();
            return;
        }

        dispatcher.Invoke(ApplyNow);
    }
}
