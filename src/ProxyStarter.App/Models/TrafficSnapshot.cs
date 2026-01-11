namespace ProxyStarter.App.Models;

public sealed record TrafficSnapshot(
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    long UploadTotalBytes,
    long DownloadTotalBytes);
