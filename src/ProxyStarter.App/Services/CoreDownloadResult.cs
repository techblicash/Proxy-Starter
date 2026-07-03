namespace ProxyStarter.App.Services;

public sealed record CoreDownloadResult(
    CoreDefinition Core,
    string Version,
    string AssetName,
    string InstalledPath);
