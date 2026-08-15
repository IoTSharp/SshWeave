using SshWeave.Configuration;
using SshWeave.Networking;
using SshWeave.Tun;

namespace SshWeave.Tests;

public sealed class WindowsTunCommandBuilderTests
{
    [Fact]
    public void BuildsPinnedTun2SocksDataPathArguments()
    {
        TransparentTcpRoute route = CreateRoute();
        SocksForward socks = new() { ListenAddress = "127.0.0.1", Port = 1080 };

        IReadOnlyList<string> arguments = WindowsTunCommandBuilder.BuildTun2Socks(route, socks);

        Assert.Equal(
            ["--device", "tun://SshWeave", "--proxy", "socks5://127.0.0.1:1080", "--mtu", "1500", "--loglevel", "info"],
            arguments);
    }

    [Fact]
    public void BuildsActiveAddressAndRouteCommands()
    {
        TransparentTcpRoute route = CreateRoute();
        _ = Ipv4Cidr.TryParse("10.51.0.0/16", true, out Ipv4Cidr destination, out _);

        IReadOnlyList<string> address = WindowsTunCommandBuilder.BuildSetAddress(route);
        IReadOnlyList<string> addRoute = WindowsTunCommandBuilder.BuildAddRoute(route, destination);
        IReadOnlyList<string> deleteRoute = WindowsTunCommandBuilder.BuildDeleteRoute(route, destination);

        Assert.Contains("address=198.18.0.1", address);
        Assert.Contains("mask=255.255.255.252", address);
        Assert.Contains("store=active", address);
        Assert.Contains("prefix=10.51.0.0/16", addRoute);
        Assert.Contains("metric=5", addRoute);
        Assert.DoesNotContain("store=persistent", addRoute);
        Assert.Contains("prefix=10.51.0.0/16", deleteRoute);
    }

    [Theory]
    [InlineData("10.*.*.*", "10.0.0.0/8")]
    [InlineData("10.165.*.*", "10.165.0.0/16")]
    [InlineData("10.51.11.*", "10.51.11.0/24")]
    [InlineData("10.51.11.132", "10.51.11.132/32")]
    public void NormalizesRouteExpressionsToCidr(string expression, string expected)
    {
        bool parsed = Ipv4Cidr.TryParseRouteExpression(expression, out Ipv4Cidr destination, out string error);

        Assert.True(parsed, error);
        Assert.Equal(expected, destination.ToString());
    }

    [Fact]
    public void ParsesDistinctRouteAliases()
    {
        IReadOnlyList<string> aliases = TransparentTcpSession.ParseRouteAliases(
            "SshWeave\r\nEthernet\r\nSshWeave\r\n");

        Assert.Equal(["SshWeave", "Ethernet"], aliases);
    }

    [Fact]
    public async Task MissingWindowsRouteIsNotReportedAsPreflightFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = Ipv4Cidr.TryParse("192.0.2.255/32", true, out Ipv4Cidr destination, out _);

        IReadOnlyList<string> aliases = await TransparentTcpSession.FindRouteAliasesAsync(
            destination,
            TestContext.Current.CancellationToken);

        Assert.NotNull(aliases);
    }

    private static TransparentTcpRoute CreateRoute() => new()
    {
        Enabled = true,
        DestinationCidrs = ["10.51.0.0/16"],
    };
}
