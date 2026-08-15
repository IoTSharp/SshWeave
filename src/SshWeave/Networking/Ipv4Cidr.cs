using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SshWeave.Networking;

internal readonly record struct Ipv4Cidr(uint Network, int PrefixLength)
{
    public IPAddress NetworkAddress => FromUInt32(Network);

    public IPAddress Netmask => FromUInt32(PrefixMask(PrefixLength));

    public static bool TryParseRouteExpression(
        string? value,
        out Ipv4Cidr cidr,
        out string error)
    {
        string expression = value?.Trim() ?? string.Empty;
        if (expression.Contains('/', StringComparison.Ordinal))
        {
            return TryParse(expression, requireNetworkAddress: true, out cidr, out error);
        }

        string[] octets = expression.Split('.');
        if (octets.Length != 4)
        {
            cidr = default;
            error = "必须是有效的 IPv4 CIDR、单个 IPv4 地址或连续尾部通配表达式（如 10.51.*.*）。";
            return false;
        }

        uint network = 0;
        int prefixLength = 32;
        bool wildcardReached = false;
        for (int index = 0; index < octets.Length; index++)
        {
            string octet = octets[index];
            if (octet == "*")
            {
                if (!wildcardReached)
                {
                    prefixLength = index * 8;
                    wildcardReached = true;
                }
                network <<= 8;
                continue;
            }

            if (wildcardReached)
            {
                cidr = default;
                error = "通配符只能连续出现在末尾，例如 10.*.*.* 或 10.165.*.*。";
                return false;
            }
            if (!byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out byte parsed))
            {
                cidr = default;
                error = "必须是有效的 IPv4 CIDR、单个 IPv4 地址或连续尾部通配表达式（如 10.51.*.*）。";
                return false;
            }
            network = (network << 8) | parsed;
        }

        cidr = new Ipv4Cidr(network, prefixLength);
        error = string.Empty;
        return true;
    }

    public static bool TryParse(
        string? value,
        bool requireNetworkAddress,
        out Ipv4Cidr cidr,
        out string error)
    {
        // 路由输入统一归一为网络序整数，避免字符串比较漏掉等价或重叠网段。
        cidr = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "不能为空。";
            return false;
        }

        string[] parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out int prefixLength)
            || prefixLength is < 0 or > 32)
        {
            error = "必须是有效的 IPv4 CIDR。";
            return false;
        }

        uint addressValue = ToUInt32(address);
        uint mask = PrefixMask(prefixLength);
        uint network = addressValue & mask;
        if (requireNetworkAddress && network != addressValue)
        {
            error = $"必须使用规范网络地址 {FromUInt32(network)}/{prefixLength}。";
            return false;
        }

        cidr = new Ipv4Cidr(network, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && (ToUInt32(address) & PrefixMask(PrefixLength)) == Network;

    public bool Overlaps(Ipv4Cidr other)
    {
        int sharedPrefix = Math.Min(PrefixLength, other.PrefixLength);
        uint mask = PrefixMask(sharedPrefix);
        return (Network & mask) == (other.Network & mask);
    }

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";

    internal static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != bytes.Length)
        {
            throw new ArgumentException("地址必须是 IPv4。", nameof(address));
        }
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static uint PrefixMask(int prefixLength) => prefixLength == 0
        ? 0
        : uint.MaxValue << (32 - prefixLength);

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
