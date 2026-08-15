using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SshWeave.Configuration;
using SshWeave.Processes;

namespace SshWeave.Tests;

public sealed class EncryptedConnectionFileTests
{
    [Fact]
    public async Task CurrentUserFileRoundTripsEmbeddedConnectionMaterial()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"sshweave-connection-tests-{Guid.NewGuid():N}");
        string keyPath = Path.Combine(directory, "id_test");
        string publicKeyPath = keyPath + ".pub";
        string knownHostsPath = Path.Combine(directory, "known_hosts");
        string connectionPath = Path.Combine(directory, "station.sshweave");
        Directory.CreateDirectory(directory);
        try
        {
            const string privateKey = "private-key-material-test-only\n";
            const string publicKey = "ssh-ed25519 public-key-material-test-only\n";
            const string knownHosts = "[192.0.2.10]:2222 ssh-ed25519 TESTONLY\n";
            await File.WriteAllTextAsync(keyPath, privateKey, Encoding.UTF8, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                publicKeyPath,
                publicKey,
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                knownHostsPath,
                knownHosts,
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
            SshProfile profile = TestConfiguration.Create().Profiles[0];
            profile.Host = "192.0.2.10";
            profile.Port = 2222;
            profile.AuthenticationMode = AuthenticationModes.KeyFile;
            profile.IdentityFile = keyPath;
            profile.KnownHostsFile = knownHostsPath;
            profile.HostKeyPolicy = HostKeyPolicies.Strict;

            await EncryptedConnectionFile.CreateAsync(
                connectionPath,
                profile,
                keyPath,
                knownHostsPath,
                authenticationSecret: "test-secret",
                overwrite: false,
                TestContext.Current.CancellationToken);

            byte[] encrypted = await File.ReadAllBytesAsync(
                connectionPath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("test-only", Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);

            string materializedDirectory;
            using (OpenedConnectionFile opened = await EncryptedConnectionFile.OpenAsync(
                connectionPath,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal("192.0.2.10", opened.Profile.Host);
                Assert.Equal("test-secret", opened.AuthenticationSecret);
                Assert.Equal(
                    privateKey,
                    await File.ReadAllTextAsync(
                        opened.Profile.IdentityFile!,
                        TestContext.Current.CancellationToken));
                Assert.Equal(
                    publicKey,
                    await File.ReadAllTextAsync(
                        opened.Profile.IdentityFile! + ".pub",
                        TestContext.Current.CancellationToken));
                Assert.Equal(
                    knownHosts,
                    await File.ReadAllTextAsync(
                        opened.Profile.KnownHostsFile!,
                        TestContext.Current.CancellationToken));
                materializedDirectory = Path.GetDirectoryName(opened.Profile.IdentityFile!)!;
                AssertPrivateMaterialAcl(materializedDirectory, opened.Profile.IdentityFile!);
            }

            Assert.False(Directory.Exists(materializedDirectory));
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
    public async Task MaterializedPrivateKeyIsAcceptedByWindowsOpenSsh()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string sshKeygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
        if (!File.Exists(sshKeygen))
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"sshweave-openssh-tests-{Guid.NewGuid():N}");
        string keyPath = Path.Combine(directory, "id_test");
        string connectionPath = Path.Combine(directory, "station.sshweave");
        Directory.CreateDirectory(directory);
        try
        {
            ProcessResult generated = await ProcessExecutor.RunCapturedAsync(
                sshKeygen,
                ["-q", "-t", "ed25519", "-N", string.Empty, "-f", keyPath],
                TestContext.Current.CancellationToken);
            Assert.True(generated.Succeeded, generated.StandardError);

            SshProfile profile = TestConfiguration.Create().Profiles[0];
            profile.AuthenticationMode = AuthenticationModes.KeyFile;
            profile.IdentityFile = keyPath;
            await EncryptedConnectionFile.CreateAsync(
                connectionPath,
                profile,
                keyPath,
                knownHostsPath: null,
                authenticationSecret: null,
                overwrite: false,
                TestContext.Current.CancellationToken);

            using OpenedConnectionFile opened = await EncryptedConnectionFile.OpenAsync(
                connectionPath,
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(opened.Profile.IdentityFile! + ".pub"));
            // 行为断言使用产品依赖的系统 OpenSSH，直接覆盖其 Windows 私钥权限门禁。
            ProcessResult inspected = await ProcessExecutor.RunCapturedAsync(
                sshKeygen,
                ["-y", "-f", opened.Profile.IdentityFile!],
                TestContext.Current.CancellationToken);

            Assert.True(inspected.Succeeded, inspected.StandardError);
            Assert.StartsWith("ssh-ed25519 ", inspected.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivateMaterialAcl(string directoryPath, string identityPath)
    {
        // 回归测试只允许产品定义的三个 Windows 主体，防止再次继承沙箱或 Users 读取权限。
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string currentUserSid = Assert.IsType<SecurityIdentifier>(identity.User).Value;
        HashSet<string> allowedSids =
        [
            currentUserSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null).Value,
        ];

        DirectorySecurity directorySecurity = new DirectoryInfo(directoryPath)
            .GetAccessControl(AccessControlSections.Access);
        Assert.True(directorySecurity.AreAccessRulesProtected);
        AssertOnlyAllowedPrincipals(directorySecurity, allowedSids);

        FileSecurity fileSecurity = new FileInfo(identityPath)
            .GetAccessControl(AccessControlSections.Access);
        AssertOnlyAllowedPrincipals(fileSecurity, allowedSids);
    }

    [SupportedOSPlatform("windows")]
    // 显式和继承规则都纳入检查，确保文件级 ACL 不偷偷放宽主体范围。
    private static void AssertOnlyAllowedPrincipals(
        FileSystemSecurity security,
        IReadOnlySet<string> allowedSids)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        Assert.NotEmpty(rules);
        foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>())
        {
            SecurityIdentifier principal = Assert.IsType<SecurityIdentifier>(rule.IdentityReference);
            Assert.Contains(principal.Value, allowedSids);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
    }
}
