using SshWeave.Ssh;

namespace SshWeave.Tests;

public sealed class Socks5BridgeTests
{
    [Fact]
    public void BuildConnectRequestEncodesIpv4AndPortInNetworkOrder()
    {
        byte[] request = Socks5Bridge.BuildConnectRequest("10.20.0.10", 22);

        Assert.Equal(new byte[] { 5, 1, 0, 1, 10, 20, 0, 10, 0, 22 }, request);
    }

    [Fact]
    public void BuildConnectRequestLeavesDomainResolutionAtSshServer()
    {
        byte[] request = Socks5Bridge.BuildConnectRequest("device.internal", 443);

        Assert.Equal(3, request[3]);
        Assert.Equal("device.internal".Length, request[4]);
        Assert.Equal(1, request[^2]);
        Assert.Equal(187, request[^1]);
    }

    [Fact]
    public void BuildConnectRequestConvertsInternationalizedDomainToAscii()
    {
        byte[] request = Socks5Bridge.BuildConnectRequest("设备.example", 80);
        string encodedHost = System.Text.Encoding.ASCII.GetString(request, 5, request[4]);

        Assert.Equal(new System.Globalization.IdnMapping().GetAscii("设备.example"), encodedHost);
    }

    [Fact]
    public void RenderProxyCommandReusesActiveSocksConnection()
    {
        string result = OpenSshConfigRenderer.RenderProxyCommand(
            "10.* 192.168.*",
            "sshweave",
            "config.json",
            TestConfiguration.Create().Profiles[0]);

        Assert.Contains("Host 10.* 192.168.*", result, StringComparison.Ordinal);
        Assert.Contains("socks-connect", result, StringComparison.Ordinal);
        Assert.Contains("%h %p", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderProxyCommandRejectsShellExpansionInExecutable()
    {
        Assert.Throws<SshWeave.Configuration.ConfigurationException>(() => OpenSshConfigRenderer.RenderProxyCommand(
            "10.*",
            "%TEMP%\\sshweave.exe",
            "config.json",
            TestConfiguration.Create().Profiles[0]));
    }
}
