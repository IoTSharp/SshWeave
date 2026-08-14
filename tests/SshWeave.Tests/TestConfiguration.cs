using SshWeave.Configuration;

namespace SshWeave.Tests;

internal static class TestConfiguration
{
    public static SshWeaveConfiguration Create() => new()
    {
        DefaultProfile = "station",
        Profiles =
        [
            new SshProfile
            {
                Name = "station",
                Host = "bastion.example.com",
                User = "sshweave",
                Socks = new SocksForward { Port = 1080 },
                TcpForwards =
                [
                    new TcpForward
                    {
                        LocalPort = 2222,
                        DestinationHost = "10.20.0.10",
                        DestinationPort = 22,
                    },
                ],
            },
        ],
    };
}
