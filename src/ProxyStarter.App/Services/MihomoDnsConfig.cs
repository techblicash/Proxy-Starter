using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProxyStarter.App.Services;

internal static class MihomoDnsConfig
{
    private static readonly string[] FallbackNameservers =
    {
        "223.5.5.5",
        "119.29.29.29",
        "1.1.1.1",
        "8.8.8.8"
    };

    public static Dictionary<string, object> Build(int listenPort = 1053, string listenHost = "0.0.0.0")
    {
        var nameservers = GetNameservers();
        return new Dictionary<string, object>
        {
            ["enable"] = true,
            ["listen"] = $"{listenHost}:{listenPort}",
            ["enhanced-mode"] = "fake-ip",
            ["default-nameserver"] = nameservers,
            ["nameserver"] = nameservers,
            ["proxy-server-nameserver"] = nameservers,
            ["fallback"] = FallbackNameservers,
            ["fallback-filter"] = new Dictionary<string, object>
            {
                ["geoip"] = true,
                ["geoip-code"] = "CN",
                ["ipcidr"] = new[] { "240.0.0.0/4" }
            }
        };
    }

    private static string[] GetNameservers()
    {
        var servers = new List<string>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var address in networkInterface.GetIPProperties().DnsAddresses)
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    servers.Add(address.ToString());
                }
            }
        }
        catch
        {
        }

        servers.AddRange(FallbackNameservers);
        return servers
            .Where(server => !string.IsNullOrWhiteSpace(server))
            .Distinct()
            .ToArray();
    }
}
