using System.Net;
using System.Net.Sockets;
using SshWeave.Configuration;
using SshWeave.Networking;

namespace SshWeave.Tun;

internal static class WindowsTunCommandBuilder
{
    // 所有外部进程均使用 ArgumentList；配置值不会经过 cmd.exe 或 PowerShell 字符串插值。
    public static IReadOnlyList<string> BuildTun2Socks(TransparentTcpRoute route, SocksForward socks)
    {
        string proxyHost = BuildProxyHost(socks.ListenAddress);
        return
        [
            "--device",
            $"tun://{route.AdapterName}",
            "--proxy",
            $"socks5://{proxyHost}:{socks.Port}",
            "--mtu",
            route.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--loglevel",
            "info",
        ];
    }

    public static IReadOnlyList<string> BuildSetAddress(TransparentTcpRoute route)
    {
        ParseAdapterAddress(route, out IPAddress address, out Ipv4Cidr cidr);
        return
        [
            "interface",
            "ipv4",
            "set",
            "address",
            $"name={route.AdapterName}",
            "source=static",
            $"address={address}",
            $"mask={cidr.Netmask}",
            "gateway=none",
            "store=active",
        ];
    }

    public static IReadOnlyList<string> BuildAddRoute(
        TransparentTcpRoute route,
        Ipv4Cidr destination) =>
    [
        "interface",
        "ipv4",
        "add",
        "route",
        $"prefix={destination}",
        $"interface={route.AdapterName}",
        "nexthop=0.0.0.0",
        $"metric={route.RouteMetric.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        "store=active",
    ];

    public static IReadOnlyList<string> BuildDeleteRoute(
        TransparentTcpRoute route,
        Ipv4Cidr destination) =>
    [
        "interface",
        "ipv4",
        "delete",
        "route",
        $"prefix={destination}",
        $"interface={route.AdapterName}",
        "nexthop=0.0.0.0",
        "store=active",
    ];

    public static IReadOnlyList<string> BuildDeleteAddress(TransparentTcpRoute route)
    {
        ParseAdapterAddress(route, out IPAddress address, out _);
        return
        [
            "interface",
            "ipv4",
            "delete",
            "address",
            $"name={route.AdapterName}",
            $"address={address}",
            "store=active",
        ];
    }

    public static void ParseAdapterAddress(
        TransparentTcpRoute route,
        out IPAddress address,
        out Ipv4Cidr cidr)
    {
        string[] parts = route.AdapterIpv4Cidr.Split('/', StringSplitOptions.TrimEntries);
        address = IPAddress.Parse(parts[0]);
        _ = Ipv4Cidr.TryParse(route.AdapterIpv4Cidr, false, out cidr, out _);
    }

    private static string BuildProxyHost(string listenAddress)
    {
        IPAddress address = IPAddress.Parse(listenAddress);
        if (address.Equals(IPAddress.Any))
        {
            return "127.0.0.1";
        }
        if (address.Equals(IPAddress.IPv6Any))
        {
            return "[::1]";
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }
}
