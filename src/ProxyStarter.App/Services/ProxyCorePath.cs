using System;
using System.IO;

namespace ProxyStarter.App.Services;

internal static class ProxyCorePath
{
    public static string ResolveExecutable(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    public static string ResolveDataPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(AppPaths.DataDirectory, path);
    }
}
