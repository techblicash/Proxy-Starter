using CommunityToolkit.Mvvm.ComponentModel;

namespace ProxyStarter.App.Models;

public sealed partial class ProxyNode : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    public string TypeDisplay => GetTypeDisplay(Type);

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private int _latencyMs = -1;

    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(TypeDisplay));
    }

    private static string GetTypeDisplay(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return string.Empty;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "ss" => "Shadowsocks",
            "ssr" => "ShadowsocksR",
            "vmess" => "VMess",
            "vless" => "VLESS",
            "trojan" => "Trojan",
            "socks5" => "SOCKS5",
            "http" => "HTTP",
            "https" => "HTTPS",
            "wireguard" => "WireGuard",
            "hysteria" => "Hysteria",
            "hysteria2" => "Hysteria2",
            "tuic" => "TUIC",
            _ => type
        };
    }
}
