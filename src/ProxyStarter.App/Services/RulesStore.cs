using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProxyStarter.App.Services;

public sealed class RulesStore
{
    private readonly string _rulesPath;

    public RulesStore()
    {
        _rulesPath = Path.Combine(AppPaths.DataDirectory, "rules.txt");
    }

    public string LoadText()
    {
        try
        {
            return File.Exists(_rulesPath) ? File.ReadAllText(_rulesPath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<string> LoadRules()
    {
        var text = LoadText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .ToList();
    }

    public void SaveText(string? text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(_rulesPath, text ?? string.Empty, new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
