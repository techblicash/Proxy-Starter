namespace ProxyStarter.App.Models;

public sealed record MihomoConnectionSnapshot(
    long UploadTotal,
    long DownloadTotal,
    string? CurrentAddress,
    IReadOnlyList<MihomoConnection>? Connections = null);
