using SshWeave.Configuration;
using SshWeave.Ssh;

namespace SshWeave.Tests;

public sealed class SshArgumentBuilderTests
{
    [Fact]
    public void BuildUsesDynamicAndLocalTcpForwards()
    {
        SshProfile profile = TestConfiguration.Create().Profiles[0];

        IReadOnlyList<string> arguments = SshArgumentBuilder.Build(profile);

        Assert.Equal("-N", arguments[0]);
        Assert.Contains("127.0.0.1:1080", arguments);
        Assert.Contains("127.0.0.1:2222:10.20.0.10:22", arguments);
        Assert.Contains("StrictHostKeyChecking=accept-new", arguments);
        Assert.Equal("sshweave@bastion.example.com", arguments[^1]);
    }

    [Fact]
    public void BuildEnablesNonInteractivePublicKeyPolicyInBatchMode()
    {
        SshProfile profile = TestConfiguration.Create().Profiles[0];
        profile.BatchMode = true;
        profile.IdentityFile = "keys/station";

        IReadOnlyList<string> arguments = SshArgumentBuilder.Build(profile);

        Assert.Contains("BatchMode=yes", arguments);
        Assert.Contains("PasswordAuthentication=no", arguments);
        Assert.Contains("KbdInteractiveAuthentication=no", arguments);
        Assert.Contains("IdentitiesOnly=yes", arguments);
    }

    [Fact]
    public void BuildFormatsIpv6DestinationsWithoutAmbiguousColons()
    {
        SshProfile profile = TestConfiguration.Create().Profiles[0];
        profile.Host = "2001:db8::1";
        profile.TcpForwards[0].DestinationHost = "2001:db8::10";

        IReadOnlyList<string> arguments = SshArgumentBuilder.Build(profile);

        Assert.Contains("127.0.0.1:2222:[2001:db8::10]:22", arguments);
        Assert.Equal("sshweave@[2001:db8::1]", arguments[^1]);
    }

    [Fact]
    public void BuildRestrictsPasswordAuthenticationToInteractivePasswordMethods()
    {
        SshProfile profile = TestConfiguration.Create().Profiles[0];
        profile.AuthenticationMode = AuthenticationModes.Password;

        IReadOnlyList<string> arguments = SshArgumentBuilder.Build(profile);

        Assert.Contains("BatchMode=no", arguments);
        Assert.Contains("PreferredAuthentications=password,keyboard-interactive", arguments);
        Assert.Contains("PubkeyAuthentication=no", arguments);
        Assert.DoesNotContain("BatchMode=yes", arguments);
    }

    [Fact]
    public void BuildCanRedirectPublicListenersToHiddenMeteredEndpoints()
    {
        SshProfile profile = TestConfiguration.Create().Profiles[0];
        SshRuntimeForwardPlan plan = new(
            new SocksForward { Port = 41080 },
            [
                new TcpForward
                {
                    LocalPort = 42222,
                    DestinationHost = "10.20.0.10",
                    DestinationPort = 22,
                },
            ]);

        IReadOnlyList<string> arguments = SshArgumentBuilder.Build(profile, runtimeForwards: plan);

        Assert.Contains("127.0.0.1:41080", arguments);
        Assert.Contains("127.0.0.1:42222:10.20.0.10:22", arguments);
        Assert.DoesNotContain("127.0.0.1:1080", arguments);
        Assert.DoesNotContain("127.0.0.1:2222:10.20.0.10:22", arguments);
    }
}
