namespace ProxyStarter.App.Models;

public sealed class MihomoLaunchOptions
{
    public required string CorePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string ConfigPath { get; init; }
    public string? ExtraArguments { get; init; }
}
