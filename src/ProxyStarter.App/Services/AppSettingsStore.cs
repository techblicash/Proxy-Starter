using System;
using System.IO;
using System.Text.Json;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly object _sync = new();

    public AppSettings Settings { get; private set; }

    public AppSettingsStore()
    {
        _settingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");
        Settings = LoadInternal();
    }

    public void Save()
    {
        lock (_sync)
        {
            Settings = Normalize(Settings);
            var json = JsonSerializer.Serialize(Settings, Options);
            AtomicFile.WriteAllText(_settingsPath, json);
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            Settings = LoadInternal();
        }
    }

    private AppSettings LoadInternal()
    {
        foreach (var json in AtomicFile.ReadTextCandidates(_settingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "AppSettingsStore: Load settings");
            }
        }

        return new AppSettings();
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.ConfigPath = DefaultIfBlank(settings.ConfigPath, "config.yaml");
        settings.CoreType = NormalizeCoreType(settings.CoreType, settings.CorePath, settings.ConfigPath);
        if (CoreCatalog.IsDefaultPath(settings.CorePath))
        {
            settings.CorePath = CoreCatalog.Get(settings.CoreType).RelativeExecutablePath;
        }
        else
        {
            settings.CorePath = settings.CorePath.Trim();
        }
        settings.MixedPort = NormalizePort(settings.MixedPort, 7890, allowZero: true);
        settings.HttpPort = NormalizePort(settings.HttpPort, 7891, allowZero: true);
        settings.SocksPort = NormalizePort(settings.SocksPort, 7892, allowZero: true);
        settings.ApiPort = NormalizePort(settings.ApiPort, 9090, allowZero: false);
        settings.ApiSecret ??= string.Empty;
        settings.Mode = NormalizeChoice(settings.Mode, "rule", "rule", "global", "direct");
        settings.LogLevel = NormalizeChoice(settings.LogLevel, "info", "debug", "info", "warning", "error", "silent");
        settings.LogRetentionCount = Math.Clamp(settings.LogRetentionCount, 1, MihomoLogService.MaxRetentionLimit);
        settings.SelectionGroup = DefaultIfBlank(settings.SelectionGroup, "Auto");
        settings.Theme = NormalizeChoice(settings.Theme, "Light", "Light", "Dark", "System");
        settings.PaneAcrylicOpacity = Math.Clamp(settings.PaneAcrylicOpacity, 0, 255);
        settings.ContentAcrylicOpacity = Math.Clamp(settings.ContentAcrylicOpacity, 0, 255);
        settings.FontSize = Math.Clamp(settings.FontSize, 10, 24);
        settings.BlockedSites ??= string.Empty;
        settings.UpdateFeedUrl ??= string.Empty;
        settings.DownloadLimitKbps = Math.Max(0, settings.DownloadLimitKbps);
        settings.UploadLimitKbps = Math.Max(0, settings.UploadLimitKbps);
        settings.Language = NormalizeChoice(settings.Language, "English", "English", "中文");
        return settings;
    }

    private static string NormalizeCoreType(string? coreType, string corePath, string configPath)
    {
        var normalized = string.IsNullOrWhiteSpace(coreType)
            ? string.Empty
            : coreType.Trim().ToLowerInvariant().Replace("_", "-");

        if (normalized is "singbox")
        {
            normalized = "sing-box";
        }

        if (normalized is "sing-box" or "mihomo")
        {
            if (normalized == "mihomo" && LooksLikeSingBoxCore(corePath))
            {
                return "sing-box";
            }

            return normalized;
        }

        return LooksLikeSingBoxCore(corePath) || configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "sing-box"
            : "mihomo";
    }

    private static bool LooksLikeSingBoxCore(string corePath)
    {
        var coreFileName = Path.GetFileName(corePath);
        return coreFileName.Contains("sing-box", StringComparison.OrdinalIgnoreCase)
               || coreFileName.Contains("singbox", StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizePort(int value, int defaultValue, bool allowZero)
    {
        if (allowZero && value == 0)
        {
            return 0;
        }

        return value is >= 1 and <= 65535 ? value : defaultValue;
    }

    private static string DefaultIfBlank(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        foreach (var item in allowed)
        {
            if (string.Equals(value, item, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return fallback;
    }
}
