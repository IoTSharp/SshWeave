namespace SshWeave.Configuration;

public sealed class SshWeaveConfiguration
{
    public const string CurrentSchema = "sshweave.config.v1";

    public string Schema { get; set; } = CurrentSchema;

    public string SshExecutable { get; set; } = "ssh";

    public string? Tun2SocksExecutable { get; set; }

    public string? DefaultProfile { get; set; }

    public List<SshProfile> Profiles { get; set; } = [];
}

public sealed class SshProfile
{
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    public string AuthenticationMode { get; set; } = AuthenticationModes.Auto;

    public string? IdentityFile { get; set; }

    public string? KnownHostsFile { get; set; }

    public string HostKeyPolicy { get; set; } = HostKeyPolicies.AcceptNew;

    public bool BatchMode { get; set; }

    public bool Compression { get; set; }

    public bool AllowRemoteClients { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int StartupTimeoutSeconds { get; set; } = 60;

    public int ServerAliveIntervalSeconds { get; set; } = 30;

    public int ServerAliveCountMax { get; set; } = 3;

    public SocksForward? Socks { get; set; } = new();

    public List<TcpForward> TcpForwards { get; set; } = [];

    public TransparentTcpRoute TransparentTcp { get; set; } = new();
}

public sealed class SocksForward
{
    public string ListenAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 1080;
}

public sealed class TcpForward
{
    public string ListenAddress { get; set; } = "127.0.0.1";

    public int LocalPort { get; set; }

    public string DestinationHost { get; set; } = string.Empty;

    public int DestinationPort { get; set; }
}

public sealed class TransparentTcpRoute
{
    public bool Enabled { get; set; }

    public string AdapterName { get; set; } = "SshWeave";

    public string AdapterIpv4Cidr { get; set; } = "198.18.0.1/30";

    public int Mtu { get; set; } = 1500;

    public int RouteMetric { get; set; } = 5;

    public List<string> DestinationCidrs { get; set; } = [];
}

public static class HostKeyPolicies
{
    public const string Strict = "strict";
    public const string AcceptNew = "accept-new";
}

public static class AuthenticationModes
{
    public const string Auto = "auto";
    public const string Password = "password";
    public const string KeyFile = "keyFile";
}
