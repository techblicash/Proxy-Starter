using System.IO;

namespace ProxyStarter.App.Services;

public sealed class DefaultRulesService
{
    public IReadOnlyList<string> Rules { get; } = LoadRules();

    private static IReadOnlyList<string> LoadRules()
    {
        using var stream = typeof(DefaultRulesService).Assembly
            .GetManifestResourceStream("ProxyStarter.App.Resources.DefaultRules.txt")!;
        using var reader = new StreamReader(stream);
        var rules = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                rules.Add(line);
            }
        }

        return rules.AsReadOnly();
    }
}
