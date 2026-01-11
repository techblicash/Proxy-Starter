using System;
using System.IO;

namespace ProxyStarter.App.Services;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProxyStarter");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");
}
