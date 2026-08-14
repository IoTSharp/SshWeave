using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SshWeave.Configuration;

namespace SshWeave.Ssh;

public static class Socks5Bridge
{
    public static async Task RunAsync(
        SocksForward proxy,
        string destinationHost,
        int destinationPort,
        CancellationToken cancellationToken = default)
    {
        if (!ConfigurationValidator.IsHost(destinationHost))
        {
            throw new ConfigurationException("SOCKS5 目标不是有效的主机名或 IP 地址。");
        }

        if (destinationPort is < 1 or > 65535)
        {
            throw new ConfigurationException("SOCKS5 目标端口必须在 1 到 65535 之间。");
        }

        IPAddress proxyAddress = IPAddress.Parse(proxy.ListenAddress);
        if (proxyAddress.Equals(IPAddress.Any))
        {
            proxyAddress = IPAddress.Loopback;
        }
        else if (proxyAddress.Equals(IPAddress.IPv6Any))
        {
            proxyAddress = IPAddress.IPv6Loopback;
        }

        using TcpClient client = new(proxyAddress.AddressFamily);
        await client.ConnectAsync(proxyAddress, proxy.Port, cancellationToken);
        await using NetworkStream network = client.GetStream();

        await network.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        byte[] greeting = new byte[2];
        await ReadExactlyAsync(network, greeting, cancellationToken);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            throw new ConfigurationException("本地 SOCKS5 代理拒绝无认证连接。");
        }

        byte[] request = BuildConnectRequest(destinationHost, destinationPort);
        await network.WriteAsync(request, cancellationToken);
        await ReadConnectResponseAsync(network, cancellationToken);

        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        Task upload = input.CopyToAsync(network, cancellationToken);
        Task download = network.CopyToAsync(output, cancellationToken);

        // 任一方向结束即关闭套接字，避免 ProxyCommand 在远端断开后悬挂。
        Task completed = await Task.WhenAny(upload, download);
        if (completed == upload)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // 对端同时关闭时无需再次处理。
            }

            await download;
        }
    }

    internal static byte[] BuildConnectRequest(string destinationHost, int destinationPort)
    {
        List<byte> request = [0x05, 0x01, 0x00];
        if (IPAddress.TryParse(destinationHost, out IPAddress? address))
        {
            byte[] bytes = address.GetAddressBytes();
            request.Add(address.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04);
            request.AddRange(bytes);
        }
        else
        {
            string asciiHost;
            try
            {
                asciiHost = new IdnMapping().GetAscii(destinationHost);
            }
            catch (ArgumentException)
            {
                throw new ConfigurationException("SOCKS5 目标主机名不是有效的国际化域名。");
            }

            byte[] bytes = Encoding.ASCII.GetBytes(asciiHost);
            if (bytes.Length is 0 or > 255)
            {
                throw new ConfigurationException("SOCKS5 目标主机名长度必须在 1 到 255 字节之间。");
            }

            request.Add(0x03);
            request.Add((byte)bytes.Length);
            request.AddRange(bytes);
        }

        Span<byte> port = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)destinationPort);
        request.AddRange(port.ToArray());
        return [.. request];
    }

    private static async Task ReadConnectResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (header[0] != 0x05)
        {
            throw new ConfigurationException("SOCKS5 代理返回了未知协议版本。");
        }

        if (header[1] != 0x00)
        {
            throw new ConfigurationException($"SOCKS5 目标连接失败，状态码为 0x{header[1]:X2}。");
        }

        int addressLength = header[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadDomainLengthAsync(stream, cancellationToken),
            _ => throw new ConfigurationException("SOCKS5 代理返回了未知地址类型。"),
        };
        byte[] remainder = new byte[addressLength + 2];
        await ReadExactlyAsync(stream, remainder, cancellationToken);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[1];
        await ReadExactlyAsync(stream, length, cancellationToken);
        return length[0];
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("SOCKS5 代理提前关闭了连接。");
            }

            offset += read;
        }
    }
}
