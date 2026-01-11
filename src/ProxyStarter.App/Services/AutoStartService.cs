using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ProxyStarter.App.Services;

public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "ProxyStarter";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void Apply(bool enable)
    {
        if (enable)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    private static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            key.SetValue(AppValueName, $"\"{exePath}\"");
        }
        catch
        {
        }
    }

    private static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
