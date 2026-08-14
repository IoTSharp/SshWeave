namespace SshWeave.Configuration;

public sealed class SshWeaveConfiguration
{
    public const string CurrentSchema = "sshweave.config.v1";

    public string Schema { get; set; } = CurrentSchema;

    public string SshExecutable { get; set; } = "ssh";

    public string? DefaultProfile { get; set; }

    public List<SshProfile> Profiles { get; set; } = [];
}

public sealed class SshProfile
{
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

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

public static class HostKeyPolicies
{
    public const string Strict = "strict";
    public const string AcceptNew = "accept-new";
}
