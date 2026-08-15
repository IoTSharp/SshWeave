using System.Net;
using SshWeave.Configuration;

namespace SshWeave.Ssh;

public sealed record SshRuntimeForwardPlan(
    SocksForward? Socks,
    IReadOnlyList<TcpForward> TcpForwards);

public static class SshArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        SshProfile profile,
        bool configurationDump = false,
        SshRuntimeForwardPlan? runtimeForwards = null,
        bool verbose = false,
        // 代理-only 桌面尝试不能等待终端或 SSH_ASKPASS，失败应立即返回给界面。
        bool forceBatchMode = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<string> arguments = [];
        if (configurationDump)
        {
            arguments.Add("-G");
        }
        else
        {
            arguments.Add("-N");
            arguments.Add("-T");
            if (verbose)
            {
                arguments.Add("-v");
            }
        }

        arguments.Add("-p");
        arguments.Add(profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddOption(arguments, "ExitOnForwardFailure=yes");
        AddOption(arguments, $"ConnectTimeout={profile.ConnectTimeoutSeconds}");
        AddOption(arguments, $"ServerAliveInterval={profile.ServerAliveIntervalSeconds}");
        AddOption(arguments, $"ServerAliveCountMax={profile.ServerAliveCountMax}");
        AddOption(
            arguments,
            $"StrictHostKeyChecking={(profile.HostKeyPolicy == HostKeyPolicies.Strict ? "yes" : "accept-new")}");

        if (!string.IsNullOrWhiteSpace(profile.KnownHostsFile))
        {
            AddOption(arguments, $"UserKnownHostsFile={Path.GetFullPath(profile.KnownHostsFile)}");
        }

        if (!string.IsNullOrWhiteSpace(profile.IdentityFile))
        {
            arguments.Add("-i");
            arguments.Add(Path.GetFullPath(profile.IdentityFile));
        }

        if (profile.AuthenticationMode == AuthenticationModes.Password)
        {
            AddOption(arguments, "BatchMode=no");
            AddOption(arguments, "PreferredAuthentications=password,keyboard-interactive");
            AddOption(arguments, "PubkeyAuthentication=no");
        }
        else if (profile.BatchMode || forceBatchMode)
        {
            AddOption(arguments, "BatchMode=yes");
            AddOption(arguments, "PasswordAuthentication=no");
            AddOption(arguments, "KbdInteractiveAuthentication=no");
            if (!string.IsNullOrWhiteSpace(profile.IdentityFile))
            {
                AddOption(arguments, "IdentitiesOnly=yes");
            }
        }

        if (profile.AuthenticationMode == AuthenticationModes.KeyFile)
        {
            AddOption(arguments, "IdentitiesOnly=yes");
            AddOption(arguments, "PreferredAuthentications=publickey");
        }

        if (profile.Compression)
        {
            arguments.Add("-C");
        }

        SocksForward? socks = runtimeForwards?.Socks ?? profile.Socks;
        IReadOnlyList<TcpForward> tcpForwards = runtimeForwards?.TcpForwards ?? profile.TcpForwards;
        if (socks is not null)
        {
            arguments.Add("-D");
            arguments.Add(FormatEndpoint(socks.ListenAddress, socks.Port));
        }

        foreach (TcpForward forward in tcpForwards)
        {
            arguments.Add("-L");
            arguments.Add(
                $"{FormatHost(forward.ListenAddress)}:{forward.LocalPort}:{FormatHost(forward.DestinationHost)}:{forward.DestinationPort}");
        }

        arguments.Add($"{profile.User}@{FormatDestinationHost(profile.Host)}");
        return arguments;
    }

    public static string FormatForDisplay(string executable, IEnumerable<string> arguments)
    {
        IEnumerable<string> values = new[] { executable }.Concat(arguments);
        return string.Join(' ', values.Select(QuoteForDisplay));
    }

    private static void AddOption(List<string> arguments, string option)
    {
        arguments.Add("-o");
        arguments.Add(option);
    }

    private static string FormatEndpoint(string host, int port) => $"{FormatHost(host)}:{port}";

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    private static string FormatDestinationHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    private static string QuoteForDisplay(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character is not '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
