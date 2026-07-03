using System;
using System.Collections.Generic;
using System.IO;

namespace ProxyStarter.App.Services;

public static class GeoIpDatabaseHelper
{
    public static void EnsureDatabase(string? configPath)
    {
        EnsureDatabaseFile(configPath, "Country.mmdb");
        EnsureDatabaseFile(configPath, "geoip.metadb");
    }

    private static string GetTargetDirectory(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return AppPaths.DataDirectory;
    }

    private static void EnsureDatabaseFile(string? configPath, string fileName)
    {
        try
        {
            var targetDirectory = GetTargetDirectory(configPath);
            var targetPath = Path.Combine(targetDirectory, fileName);
            if (File.Exists(targetPath))
            {
                return;
            }

            foreach (var candidate in GetCandidatePaths(fileName))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(targetDirectory);
                    File.Copy(candidate, targetPath, overwrite: false);
                    if (File.Exists(targetPath))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, $"GeoIpDatabaseHelper: Copy from {candidate}");
                }
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"GeoIpDatabaseHelper: Ensure {fileName}");
        }
    }

    private static IEnumerable<string> GetCandidatePaths(string fileName)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".config", "clash", fileName);
            yield return Path.Combine(userProfile, ".config", "mihomo", fileName);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "clash", fileName);
            yield return Path.Combine(appData, "mihomo", fileName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "clash", fileName);
            yield return Path.Combine(localAppData, "mihomo", fileName);
        }

        yield return Path.Combine(AppPaths.RuntimeDirectory, fileName);
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}
