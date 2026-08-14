using System.Net;
using SshWeave.Configuration;

namespace SshWeave.Ssh;

public static class SshArgumentBuilder
{
    public static IReadOnlyList<string> Build(SshProfile profile, bool configurationDump = false)
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

        if (profile.BatchMode)
        {
            AddOption(arguments, "BatchMode=yes");
            AddOption(arguments, "PasswordAuthentication=no");
            AddOption(arguments, "KbdInteractiveAuthentication=no");
            if (!string.IsNullOrWhiteSpace(profile.IdentityFile))
            {
                AddOption(arguments, "IdentitiesOnly=yes");
            }
        }

        if (profile.Compression)
        {
            arguments.Add("-C");
        }

        if (profile.Socks is not null)
        {
            arguments.Add("-D");
            arguments.Add(FormatEndpoint(profile.Socks.ListenAddress, profile.Socks.Port));
        }

        foreach (TcpForward forward in profile.TcpForwards)
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
