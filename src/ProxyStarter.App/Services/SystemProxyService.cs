using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class SystemProxyService
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string BackupFileName = "system-proxy-backup.json";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private readonly string _backupPath;
    private readonly object _sync = new();

    public SystemProxyService()
    {
        _backupPath = Path.Combine(AppPaths.DataDirectory, BackupFileName);
    }

    public void Apply(AppSettings settings)
    {
        try
        {
            var port = ResolveProxyPort(settings);
            if (port <= 0)
            {
                return;
            }

            lock (_sync)
            {
                using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true);
                if (key is null)
                {
                    return;
                }

                SaveBackupIfNeeded(key);

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", BuildProxyServer(port), RegistryValueKind.String);

                var proxyOverride = key.GetValue("ProxyOverride") as string;
                if (string.IsNullOrWhiteSpace(proxyOverride))
                {
                    key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                }

                NotifySystemProxyChanged();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SystemProxyService: Apply");
        }
    }

    public void Restore()
    {
        try
        {
            lock (_sync)
            {
                if (!File.Exists(_backupPath))
                {
                    return;
                }

                using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true);
                if (key is null)
                {
                    return;
                }

                if (TryLoadBackup(out var backup))
                {
                    WriteState(key, backup);
                }

                TryDeleteBackup();
                NotifySystemProxyChanged();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SystemProxyService: Restore");
        }
    }

    private void SaveBackupIfNeeded(RegistryKey key)
    {
        if (File.Exists(_backupPath))
        {
            return;
        }

        var backup = ReadState(key);
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(_backupPath, json);
    }

    private bool TryLoadBackup(out ProxyState state)
    {
        state = new ProxyState();

        try
        {
            if (!File.Exists(_backupPath))
            {
                return false;
            }

            foreach (var json in AtomicFile.ReadTextCandidates(_backupPath))
            {
                var parsed = JsonSerializer.Deserialize<ProxyState>(json);
                if (parsed is null)
                {
                    continue;
                }

                state = parsed;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SystemProxyService: TryLoadBackup");
            return false;
        }
    }

    private static ProxyState ReadState(RegistryKey key)
    {
        var proxyEnableValue = key.GetValue("ProxyEnable");
        var proxyEnable = proxyEnableValue switch
        {
            int intValue => intValue != 0,
            _ => Convert.ToInt32(proxyEnableValue ?? 0) != 0
        };

        return new ProxyState
        {
            ProxyEnable = proxyEnable,
            ProxyServer = key.GetValue("ProxyServer") as string ?? string.Empty,
            ProxyOverride = key.GetValue("ProxyOverride") as string ?? string.Empty
        };
    }

    private static void WriteState(RegistryKey key, ProxyState state)
    {
        key.SetValue("ProxyEnable", state.ProxyEnable ? 1 : 0, RegistryValueKind.DWord);

        if (string.IsNullOrWhiteSpace(state.ProxyServer))
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyServer", state.ProxyServer, RegistryValueKind.String);
        }

        if (string.IsNullOrWhiteSpace(state.ProxyOverride))
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyOverride", state.ProxyOverride, RegistryValueKind.String);
        }
    }

    private void TryDeleteBackup()
    {
        try
        {
            if (File.Exists(_backupPath))
            {
                File.Delete(_backupPath);
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SystemProxyService: TryDeleteBackup");
        }
    }

    private static int ResolveProxyPort(AppSettings settings)
    {
        if (settings.MixedPort > 0)
        {
            return settings.MixedPort;
        }

        if (settings.HttpPort > 0)
        {
            return settings.HttpPort;
        }

        if (settings.SocksPort > 0)
        {
            return settings.SocksPort;
        }

        return 0;
    }

    private static string BuildProxyServer(int port)
    {
        return $"127.0.0.1:{port}";
    }

    private static void NotifySystemProxyChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private sealed class ProxyState
    {
        public bool ProxyEnable { get; set; }
        public string ProxyServer { get; set; } = string.Empty;
        public string ProxyOverride { get; set; } = string.Empty;
    }
}
