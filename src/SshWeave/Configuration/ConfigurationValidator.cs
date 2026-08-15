using System.Net;
using System.Text.RegularExpressions;
using SshWeave.Networking;

namespace SshWeave.Configuration;

public static partial class ConfigurationValidator
{
    public static void Validate(SshWeaveConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> errors = [];
        if (!string.Equals(configuration.Schema, SshWeaveConfiguration.CurrentSchema, StringComparison.Ordinal))
        {
            errors.Add($"schema 必须为 {SshWeaveConfiguration.CurrentSchema}。");
        }

        if (string.IsNullOrWhiteSpace(configuration.SshExecutable) || ContainsControlCharacter(configuration.SshExecutable))
        {
            errors.Add("sshExecutable 不能为空或包含控制字符。");
        }
        ValidatePath(configuration.Tun2SocksExecutable, "tun2SocksExecutable", errors);

        if (configuration.Profiles is null || configuration.Profiles.Count == 0)
        {
            errors.Add("至少需要一个连接配置 profiles。");
        }

        HashSet<string> profileNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SshProfile? profile in configuration.Profiles ?? [])
        {
            if (profile is null)
            {
                errors.Add("profiles 不能包含 null。");
                continue;
            }

            ValidateProfile(profile, errors);
            if (!profileNames.Add(profile.Name))
            {
                errors.Add($"连接配置名称重复：{profile.Name}。");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.DefaultProfile)
            && !profileNames.Contains(configuration.DefaultProfile))
        {
            errors.Add($"defaultProfile 不存在：{configuration.DefaultProfile}。");
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationException(string.Join(Environment.NewLine, errors));
        }
    }

    public static SshProfile ResolveProfile(SshWeaveConfiguration configuration, string? requestedName)
    {
        Validate(configuration);
        string? name = requestedName ?? configuration.DefaultProfile;

        if (string.IsNullOrWhiteSpace(name))
        {
            if (configuration.Profiles.Count == 1)
            {
                return configuration.Profiles[0];
            }

            throw new ConfigurationException("存在多个连接配置，请使用 --profile 指定一个名称。");
        }

        return configuration.Profiles.FirstOrDefault(
                profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConfigurationException($"找不到连接配置：{name}。");
    }

    private static void ValidateProfile(SshProfile profile, List<string> errors)
    {
        string prefix = string.IsNullOrWhiteSpace(profile.Name) ? "profiles" : $"profiles[{profile.Name}]";
        if (!ProfileNameRegex().IsMatch(profile.Name ?? string.Empty))
        {
            errors.Add($"{prefix}.name 只能包含字母、数字、点、下划线和连字符。");
        }

        if (!IsHost(profile.Host ?? string.Empty))
        {
            errors.Add($"{prefix}.host 不是有效的主机名或 IP 地址。");
        }

        if (!UserNameRegex().IsMatch(profile.User ?? string.Empty))
        {
            errors.Add($"{prefix}.user 不是安全的 SSH 用户名。");
        }

        if (profile.AuthenticationMode is not AuthenticationModes.Auto
            and not AuthenticationModes.Password
            and not AuthenticationModes.KeyFile)
        {
            errors.Add($"{prefix}.authenticationMode 只允许 auto、password 或 keyFile。");
        }

        if (profile.AuthenticationMode == AuthenticationModes.Password && profile.BatchMode)
        {
            errors.Add($"{prefix} 使用密码认证时不能启用 batchMode。");
        }

        if (profile.AuthenticationMode == AuthenticationModes.KeyFile
            && string.IsNullOrWhiteSpace(profile.IdentityFile))
        {
            errors.Add($"{prefix} 使用 keyFile 认证时必须设置 identityFile。");
        }

        ValidatePort(profile.Port, $"{prefix}.port", errors);
        ValidateRange(profile.ConnectTimeoutSeconds, 1, 120, $"{prefix}.connectTimeoutSeconds", errors);
        ValidateRange(profile.StartupTimeoutSeconds, 5, 600, $"{prefix}.startupTimeoutSeconds", errors);
        ValidateRange(profile.ServerAliveIntervalSeconds, 1, 3600, $"{prefix}.serverAliveIntervalSeconds", errors);
        ValidateRange(profile.ServerAliveCountMax, 1, 20, $"{prefix}.serverAliveCountMax", errors);

        if (profile.HostKeyPolicy is not HostKeyPolicies.Strict and not HostKeyPolicies.AcceptNew)
        {
            errors.Add($"{prefix}.hostKeyPolicy 只允许 strict 或 accept-new，禁止关闭主机密钥校验。");
        }

        ValidatePath(profile.IdentityFile, $"{prefix}.identityFile", errors);
        ValidatePath(profile.KnownHostsFile, $"{prefix}.knownHostsFile", errors);

        if (profile.Socks is null && (profile.TcpForwards is null || profile.TcpForwards.Count == 0))
        {
            errors.Add($"{prefix} 至少需要 socks 或一条 tcpForwards。");
        }

        HashSet<string> localEndpoints = new(StringComparer.OrdinalIgnoreCase);
        if (profile.Socks is not null)
        {
            ValidateListenAddress(
                profile.Socks.ListenAddress ?? string.Empty,
                profile.AllowRemoteClients,
                $"{prefix}.socks",
                errors);
            ValidatePort(profile.Socks.Port, $"{prefix}.socks.port", errors);
            localEndpoints.Add($"{profile.Socks.ListenAddress}:{profile.Socks.Port}");
        }

        foreach (TcpForward? forward in profile.TcpForwards ?? [])
        {
            if (forward is null)
            {
                errors.Add($"{prefix}.tcpForwards 不能包含 null。");
                continue;
            }

            ValidateListenAddress(
                forward.ListenAddress ?? string.Empty,
                profile.AllowRemoteClients,
                $"{prefix}.tcpForwards",
                errors);
            ValidatePort(forward.LocalPort, $"{prefix}.tcpForwards.localPort", errors);
            ValidatePort(forward.DestinationPort, $"{prefix}.tcpForwards.destinationPort", errors);
            if (!IsHost(forward.DestinationHost ?? string.Empty))
            {
                errors.Add($"{prefix}.tcpForwards.destinationHost 不是有效的主机名或 IP 地址。");
            }

            string endpoint = $"{forward.ListenAddress}:{forward.LocalPort}";
            if (!localEndpoints.Add(endpoint))
            {
                errors.Add($"{prefix} 的本地监听端点重复：{endpoint}。");
            }
        }

        ValidateTransparentTcp(profile, prefix, errors);
    }

    private static void ValidateTransparentTcp(SshProfile profile, string prefix, List<string> errors)
    {
        TransparentTcpRoute? route = profile.TransparentTcp;
        if (route is null)
        {
            errors.Add($"{prefix}.transparentTcp 不能为 null。");
            return;
        }
        if (!route.Enabled)
        {
            return;
        }

        if (profile.Socks is null)
        {
            errors.Add($"{prefix}.transparentTcp 启用时必须同时启用 socks。");
        }
        if (!AdapterNameRegex().IsMatch(route.AdapterName ?? string.Empty))
        {
            errors.Add($"{prefix}.transparentTcp.adapterName 只能包含字母、数字、空格、点、下划线和连字符。");
        }
        ValidateRange(route.Mtu, 1280, 9000, $"{prefix}.transparentTcp.mtu", errors);
        ValidateRange(route.RouteMetric, 1, 9999, $"{prefix}.transparentTcp.routeMetric", errors);

        if (!Ipv4Cidr.TryParse(
                route.AdapterIpv4Cidr,
                requireNetworkAddress: false,
                out Ipv4Cidr adapterCidr,
                out string adapterError))
        {
            errors.Add($"{prefix}.transparentTcp.adapterIpv4Cidr {adapterError}");
            return;
        }
        if (adapterCidr.PrefixLength is < 16 or > 30)
        {
            errors.Add($"{prefix}.transparentTcp.adapterIpv4Cidr 前缀长度必须在 16 到 30 之间。");
        }

        IPAddress adapterAddress = IPAddress.Parse(route.AdapterIpv4Cidr.Split('/')[0]);
        uint adapterValue = Ipv4Cidr.ToUInt32(adapterAddress);
        if (adapterValue == adapterCidr.Network)
        {
            errors.Add($"{prefix}.transparentTcp.adapterIpv4Cidr 必须指定可用主机地址，不能是网络地址。");
        }

        if (route.DestinationCidrs is null || route.DestinationCidrs.Count == 0)
        {
            errors.Add($"{prefix}.transparentTcp.destinationCidrs 至少需要一个目标网段。");
            return;
        }

        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in route.DestinationCidrs)
        {
            string field = $"{prefix}.transparentTcp.destinationCidrs";
            // 公开配置保留 destinationCidrs 字段，同时追加兼容尾部通配和单地址写法。
            if (!Ipv4Cidr.TryParseRouteExpression(value, out Ipv4Cidr destination, out string error))
            {
                errors.Add($"{field} 中的 {value ?? "null"} {error}");
                continue;
            }
            if (destination.PrefixLength == 0)
            {
                errors.Add($"{field} 禁止接管默认路由 0.0.0.0/0。");
            }
            if (destination.Overlaps(adapterCidr))
            {
                errors.Add($"{field} 中的 {destination} 与虚拟网卡地址重叠。");
            }
            if (!destinations.Add(destination.ToString()))
            {
                errors.Add($"{field} 中的目标网段重复：{destination}。");
            }
        }
    }

    private static void ValidateListenAddress(
        string value,
        bool allowRemoteClients,
        string field,
        List<string> errors)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            errors.Add($"{field}.listenAddress 必须是明确的 IP 地址。");
            return;
        }

        bool loopback = IPAddress.IsLoopback(address);
        if (!loopback && !allowRemoteClients)
        {
            errors.Add($"{field}.listenAddress 会向其它主机暴露代理；如确有需要，必须显式设置 allowRemoteClients=true。");
        }
    }

    private static void ValidatePath(string? value, string field, List<string> errors)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || ContainsControlCharacter(value)))
        {
            errors.Add($"{field} 不能为空或包含控制字符。");
        }
    }

    private static void ValidatePort(int value, string field, List<string> errors) =>
        ValidateRange(value, 1, 65535, field, errors);

    private static void ValidateRange(int value, int minimum, int maximum, string field, List<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{field} 必须在 {minimum} 到 {maximum} 之间。");
        }
    }

    internal static bool IsHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsControlCharacter(value) || value.StartsWith('-'))
        {
            return false;
        }

        return IPAddress.TryParse(value, out _)
            || Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex UserNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterNameRegex();
}
