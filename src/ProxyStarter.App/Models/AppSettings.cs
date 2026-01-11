namespace ProxyStarter.App.Models;

public sealed class AppSettings
{
    public string CorePath { get; set; } = "runtime\\mihomo.exe";
    public string ConfigPath { get; set; } = "config.yaml";
    public int MixedPort { get; set; } = 7890;
    public int HttpPort { get; set; } = 7891;
    public int SocksPort { get; set; } = 7892;
    public int ApiPort { get; set; } = 9090;
    public string ApiSecret { get; set; } = string.Empty;
    public bool AllowLan { get; set; } = true;
    public bool TunEnabled { get; set; } = false;
    public string Mode { get; set; } = "rule";
    public string LogLevel { get; set; } = "info";
    public string SelectionGroup { get; set; } = "Auto";
    public bool AutoStart { get; set; } = false;
    public bool AutoUpdate { get; set; } = true;
    public string UpdateFeedUrl { get; set; } = string.Empty;
    public string Theme { get; set; } = "Light";

    public int PaneAcrylicOpacity { get; set; } = 112;
    public int ContentAcrylicOpacity { get; set; } = 168;

    public int FontSize { get; set; } = 12;
    public string BlockedSites { get; set; } = string.Empty;
    public int DownloadLimitKbps { get; set; } = 0;
    public int UploadLimitKbps { get; set; } = 0;
    public string Language { get; set; } = "English";
}
