namespace ProxyStarter.App.Models;

public sealed record ConnectionStatusSnapshot(
    long UploadTotal,
    long DownloadTotal,
    string ActiveNode,
    string? CurrentAddress);
