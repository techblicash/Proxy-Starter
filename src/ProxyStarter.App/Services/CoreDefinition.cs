namespace ProxyStarter.App.Services;

public sealed record CoreDefinition(
    string CoreType,
    string DisplayName,
    string Repository,
    string RelativeExecutablePath,
    string DefaultConfigPath,
    string ExecutableName);
