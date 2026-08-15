using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SshWeave.Configuration;

public sealed class EncryptedConnectionPayload
{
    public const string CurrentSchema = "sshweave.connection.v1";

    public string Schema { get; set; } = CurrentSchema;

    public SshProfile Profile { get; set; } = new();

    public string? PrivateKey { get; set; }

    public string? PublicKey { get; set; }

    public string? KnownHosts { get; set; }

    public string? AuthenticationSecret { get; set; }

    public bool ConnectOnOpen { get; set; } = true;
}

public sealed class OpenedConnectionFile : IDisposable
{
    private string? _materializedDirectory;

    internal OpenedConnectionFile(
        SshProfile profile,
        string? authenticationSecret,
        bool connectOnOpen,
        string? materializedDirectory)
    {
        Profile = profile;
        AuthenticationSecret = authenticationSecret;
        ConnectOnOpen = connectOnOpen;
        _materializedDirectory = materializedDirectory;
    }

    public SshProfile Profile { get; }

    public string? AuthenticationSecret { get; }

    public bool ConnectOnOpen { get; }

    public void Dispose()
    {
        string? directory = Interlocked.Exchange(ref _materializedDirectory, null);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            // 内嵌密钥仅为 OpenSSH 会话临时展开，应用退出时连同目录一并删除。
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // OpenSSH 仍持有文件时由下次启动清理遗留会话目录。
        }
        catch (UnauthorizedAccessException)
        {
            // 不让临时目录清理失败覆盖正常的应用退出流程。
        }
    }
}

public static class EncryptedConnectionFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SSHWEAVE-CONNECTION-V1\n");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SshWeave.EncryptedConnectionFile.v1");

    public static async Task CreateAsync(
        string path,
        SshProfile profile,
        string? privateKeyPath,
        string? knownHostsPath,
        string? authenticationSecret,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ConfigurationException("SshWeave 加密连接文件当前仅支持 Windows DPAPI。");
        }
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);

        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && !overwrite)
        {
            throw new ConfigurationException($"目标连接文件已存在：{fullPath}。如需覆盖，请添加 --force。");
        }

        if (profile.AuthenticationMode == AuthenticationModes.KeyFile
            && string.IsNullOrWhiteSpace(privateKeyPath))
        {
            throw new ConfigurationException("私钥认证的连接文件必须内嵌 --identity-file 指向的私钥。");
        }

        EncryptedConnectionPayload payload = new()
        {
            Profile = profile,
            PrivateKey = await ReadOptionalTextAsync(privateKeyPath, "私钥", cancellationToken),
            PublicKey = await ReadSiblingPublicKeyAsync(privateKeyPath, cancellationToken),
            KnownHosts = await ReadOptionalTextAsync(knownHostsPath, "known_hosts", cancellationToken),
            AuthenticationSecret = string.IsNullOrEmpty(authenticationSecret) ? null : authenticationSecret,
        };

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            SshWeaveJsonContext.Default.EncryptedConnectionPayload);
        byte[] encrypted;
        try
        {
            // CurrentUser DPAPI 让连接文件只能由创建它的 Windows 用户解密。
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(Magic, cancellationToken);
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<OpenedConnectionFile> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ConfigurationException("SshWeave 加密连接文件当前仅支持 Windows DPAPI。");
        }
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"连接文件不存在：{fullPath}");
        }

        byte[] fileBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (fileBytes.Length <= Magic.Length || !fileBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new ConfigurationException("文件不是有效的 SshWeave 加密连接文件。");
        }

        byte[] encrypted = fileBytes[Magic.Length..];
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new ConfigurationException("当前 Windows 用户无法解密此连接文件。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
            CryptographicOperations.ZeroMemory(encrypted);
        }

        EncryptedConnectionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                plaintext,
                SshWeaveJsonContext.Default.EncryptedConnectionPayload)
                ?? throw new ConfigurationException("加密连接文件内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationException("加密连接文件内容无效。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (!string.Equals(payload.Schema, EncryptedConnectionPayload.CurrentSchema, StringComparison.Ordinal))
        {
            throw new ConfigurationException($"不支持的连接文件模式：{payload.Schema}");
        }

        string? sessionDirectory = null;
        try
        {
            if (payload.PrivateKey is not null || payload.PublicKey is not null || payload.KnownHosts is not null)
            {
                sessionDirectory = CreateSessionDirectory();
            }
            if (payload.PrivateKey is not null)
            {
                string identityPath = Path.Combine(sessionDirectory!, "identity");
                await File.WriteAllTextAsync(identityPath, payload.PrivateKey, new UTF8Encoding(false), cancellationToken);
                if (payload.PublicKey is not null)
                {
                    // OpenSSH 通过同名 .pub 文件把受口令私钥与 ssh-agent 中的公钥身份关联起来。
                    await File.WriteAllTextAsync(
                        identityPath + ".pub",
                        payload.PublicKey,
                        new UTF8Encoding(false),
                        cancellationToken);
                }
                payload.Profile.IdentityFile = identityPath;
            }
            if (payload.KnownHosts is not null)
            {
                string knownHostsPath = Path.Combine(sessionDirectory!, "known_hosts");
                await File.WriteAllTextAsync(knownHostsPath, payload.KnownHosts, new UTF8Encoding(false), cancellationToken);
                payload.Profile.KnownHostsFile = knownHostsPath;
            }

            ValidateProfile(payload.Profile);
            return new OpenedConnectionFile(
                payload.Profile,
                payload.AuthenticationSecret,
                payload.ConnectOnOpen,
                sessionDirectory);
        }
        catch
        {
            if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }
            throw;
        }
    }

    private static async Task<string?> ReadOptionalTextAsync(
        string? path,
        string label,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"{label}文件不存在：{fullPath}");
        }
        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    // 可选携带同名公钥，避免 OpenSSH 在受口令私钥无法解密时无法匹配 ssh-agent 身份。
    private static async Task<string?> ReadSiblingPublicKeyAsync(
        string? privateKeyPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            return null;
        }

        string publicKeyPath = Path.GetFullPath(privateKeyPath) + ".pub";
        return File.Exists(publicKeyPath)
            ? await File.ReadAllTextAsync(publicKeyPath, cancellationToken)
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static string CreateSessionDirectory()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SshWeave",
            "Sessions");
        Directory.CreateDirectory(root);

        // 顺手清理七天前未被占用的异常退出目录，不触碰当前会话。
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        string sessionDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        DirectoryInfo sessionDirectoryInfo = new(sessionDirectory);
        sessionDirectoryInfo.Create(CreateSessionDirectorySecurity());
        return sessionDirectoryInfo.FullName;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateSessionDirectorySecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier currentUser = identity.User
            ?? throw new ConfigurationException("无法确定当前 Windows 用户，不能安全展开私钥。");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // 私钥必须在写入前就位于受保护目录，避免继承本机 Users 或沙箱组的读取权限而被 OpenSSH 拒绝。
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (SecurityIdentifier principal in new[]
        {
            currentUser,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                principal,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static void ValidateProfile(SshProfile profile)
    {
        SshWeaveConfiguration configuration = new()
        {
            DefaultProfile = profile.Name,
            Profiles = [profile],
        };
        ConfigurationValidator.Validate(configuration);
    }

}
