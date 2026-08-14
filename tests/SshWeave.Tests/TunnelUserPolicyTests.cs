using SshWeave.Configuration;
using SshWeave.Server;

namespace SshWeave.Tests;

public sealed class TunnelUserPolicyTests
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGV4YW1wbGVrZXltYXRlcmlhbA== tunnel@example";

    [Fact]
    public void RenderSshdConfigAllowsOnlyClientTcpForwarding()
    {
        string configuration = TunnelUserPolicy.RenderSshdConfig("sshweave");

        Assert.Contains("AllowTcpForwarding local", configuration, StringComparison.Ordinal);
        Assert.Contains("MaxSessions 0", configuration, StringComparison.Ordinal);
        Assert.Contains("PermitTunnel no", configuration, StringComparison.Ordinal);
        Assert.Contains("PasswordAuthentication no", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceCommand internal-sftp", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictPublicKeyAddsRestrictAndPortForwardingOnly()
    {
        string result = TunnelUserPolicy.RestrictPublicKey(PublicKey);

        Assert.Equal($"restrict,port-forwarding {PublicKey}", result);
    }

    [Fact]
    public void RestrictPublicKeyRejectsPreconfiguredOptions()
    {
        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => TunnelUserPolicy.RestrictPublicKey($"command=\"bash\" {PublicKey}"));

        Assert.Contains("公钥", exception.Message, StringComparison.Ordinal);
    }
}
