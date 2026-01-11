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

    public AppSettings Settings { get; private set; }

    public AppSettingsStore()
    {
        _settingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");
        Settings = LoadInternal();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(Settings, Options);
        File.WriteAllText(_settingsPath, json);
    }

    public void Reload()
    {
        Settings = LoadInternal();
    }

    private AppSettings LoadInternal()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
