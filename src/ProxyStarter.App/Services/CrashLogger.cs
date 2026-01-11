using System;
using System.IO;
using System.Text;

namespace ProxyStarter.App.Services;

public static class CrashLogger
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(AppPaths.LogsDirectory, "crash.log");

    public static void Log(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);

            var builder = new StringBuilder();
            builder.AppendLine("============================================================");
            builder.AppendLine(DateTimeOffset.Now.ToString("O"));
            builder.AppendLine(context);
            builder.AppendLine(exception.ToString());
            builder.AppendLine();

            lock (Gate)
            {
                File.AppendAllText(LogPath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }
}

