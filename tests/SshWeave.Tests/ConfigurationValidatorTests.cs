using SshWeave.Configuration;

namespace SshWeave.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void ResolveProfileReturnsConfiguredDefault()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();

        SshProfile profile = ConfigurationValidator.ResolveProfile(configuration, requestedName: null);

        Assert.Equal("station", profile.Name);
    }

    [Fact]
    public void ValidateRejectsDisabledHostKeyChecking()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].HostKeyPolicy = "no";

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("禁止关闭主机密钥校验", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsNonLoopbackListenerWithoutExplicitOptIn()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].Socks!.ListenAddress = "0.0.0.0";

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("allowRemoteClients=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsProfilesWithoutAnyForward()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].Socks = null;
        configuration.Profiles[0].TcpForwards.Clear();

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("至少需要 socks", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsPasswordAuthenticationInBatchMode()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].AuthenticationMode = AuthenticationModes.Password;
        configuration.Profiles[0].BatchMode = true;

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("密码认证时不能启用 batchMode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRequiresIdentityFileForKeyAuthentication()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].AuthenticationMode = AuthenticationModes.KeyFile;

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("必须设置 identityFile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsTransparentTcpForStationCidr()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].TransparentTcp = new TransparentTcpRoute
        {
            Enabled = true,
            DestinationCidrs = ["10.51.0.0/16"],
        };

        ConfigurationValidator.Validate(configuration);
    }

    [Fact]
    public void ValidateRequiresSocksForTransparentTcp()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].Socks = null;
        configuration.Profiles[0].TransparentTcp = new TransparentTcpRoute
        {
            Enabled = true,
            DestinationCidrs = ["10.51.0.0/16"],
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("transparentTcp 启用时必须同时启用 socks", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0/0", "禁止接管默认路由")]
    [InlineData("10.51.12.35/16", "必须使用规范网络地址")]
    [InlineData("198.18.0.0/24", "与虚拟网卡地址重叠")]
    public void ValidateRejectsUnsafeTransparentTcpCidr(string cidr, string expectedMessage)
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles[0].TransparentTcp = new TransparentTcpRoute
        {
            Enabled = true,
            DestinationCidrs = [cidr],
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsNullCollectionsFromUntrustedJson()
    {
        SshWeaveConfiguration configuration = TestConfiguration.Create();
        configuration.Profiles = null!;

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigurationValidator.Validate(configuration));

        Assert.Contains("至少需要一个连接配置", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExampleConfigurationRoundTripsThroughSourceGeneratedJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sshweave-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            await ConfigurationStore.WriteExampleAsync(
                path,
                overwrite: false,
                TestContext.Current.CancellationToken);

            SshWeaveConfiguration configuration = await ConfigurationStore.LoadAsync(
                path,
                TestContext.Current.CancellationToken);
            ConfigurationValidator.Validate(configuration);

            Assert.Equal(SshWeaveConfiguration.CurrentSchema, configuration.Schema);
            Assert.Equal("station", configuration.DefaultProfile);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAtomicallyReplacesConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sshweave-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            SshWeaveConfiguration configuration = TestConfiguration.Create();
            await ConfigurationStore.SaveAsync(path, configuration, TestContext.Current.CancellationToken);
            configuration.Profiles[0].Port = 2222;
            await ConfigurationStore.SaveAsync(path, configuration, TestContext.Current.CancellationToken);

            SshWeaveConfiguration loaded = await ConfigurationStore.LoadAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal(2222, loaded.Profiles[0].Port);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
