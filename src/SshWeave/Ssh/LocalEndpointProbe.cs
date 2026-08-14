using System.Net;
using System.Net.Sockets;
using SshWeave.Configuration;

namespace SshWeave.Ssh;

public static class LocalEndpointProbe
{
    public static void EnsureAvailable(SshProfile profile)
    {
        List<(string Address, int Port)> endpoints = [];
        if (profile.Socks is not null)
        {
            endpoints.Add((profile.Socks.ListenAddress, profile.Socks.Port));
        }

        endpoints.AddRange(profile.TcpForwards.Select(forward => (forward.ListenAddress, forward.LocalPort)));

        foreach ((string addressText, int port) in endpoints)
        {
            IPAddress address = IPAddress.Parse(addressText);
            using Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
            };

            try
            {
                socket.Bind(new IPEndPoint(address, port));
            }
            catch (SocketException exception)
            {
                throw new ConfigurationException($"本地监听端点不可用：{addressText}:{port}（{exception.SocketErrorCode}）。");
            }
        }
    }
}
