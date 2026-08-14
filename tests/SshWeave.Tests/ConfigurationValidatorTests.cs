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
}
