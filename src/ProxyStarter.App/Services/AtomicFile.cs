using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProxyStarter.App.Services;

internal static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static void WriteAllText(string path, string? contents, Encoding? encoding = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? "." : directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, contents ?? string.Empty, encoding ?? Utf8NoBom);

        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, GetBackupPath(path), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Copy(path, GetBackupPath(path), overwrite: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding? encoding = null)
    {
        WriteAllText(path, string.Join(Environment.NewLine, lines ?? Enumerable.Empty<string>()), encoding);
    }

    public static string ReadAllTextOrEmpty(string path, Encoding? encoding = null)
    {
        return ReadTextCandidates(path, encoding).FirstOrDefault() ?? string.Empty;
    }

    public static IReadOnlyList<string> ReadTextCandidates(string path, Encoding? encoding = null)
    {
        var result = new List<string>(capacity: 2);
        var textEncoding = encoding ?? Encoding.UTF8;

        foreach (var candidate in new[] { path, GetBackupPath(path) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    result.Add(File.ReadAllText(candidate, textEncoding));
                }
            }
            catch
            {
            }
        }

        return result;
    }

    public static string GetBackupPath(string path)
    {
        return path + ".bak";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
