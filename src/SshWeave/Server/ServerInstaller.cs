using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using SshWeave.Configuration;
using SshWeave.Processes;

namespace SshWeave.Server;

public sealed record ServerInstallRequest(
    string UserName,
    string AuthorizedKeyPath,
    string SshdConfigDirectory,
    bool SkipReload);

public sealed record ServerInstallResult(
    string UserName,
    string HomeDirectory,
    string SshdConfigPath,
    bool Reloaded);

public static class ServerInstaller
{
    private static readonly string[] RequiredEffectiveSettings =
    [
        "allowagentforwarding no",
        "allowtcpforwarding local",
        "allowstreamlocalforwarding no",
        "authenticationmethods publickey",
        "gatewayports no",
        "kbdinteractiveauthentication no",
        "maxsessions 0",
        "passwordauthentication no",
        "permittty no",
        "permittunnel no",
        "permituserrc no",
        "pubkeyauthentication yes",
        "x11forwarding no",
    ];

    public static async Task<ServerInstallResult> InstallAsync(
        ServerInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new ConfigurationException("server-install 只能在 Linux OpenSSH 服务器上运行。");
        }

        TunnelUserPolicy.ValidateUserName(request.UserName);
        if (!Path.IsPathFullyQualified(request.SshdConfigDirectory))
        {
            throw new ConfigurationException("--sshd-config-dir 必须是绝对路径。");
        }

        string publicKey = await File.ReadAllTextAsync(request.AuthorizedKeyPath, cancellationToken);
        string restrictedKey = TunnelUserPolicy.RestrictPublicKey(publicKey);
        string configPath = Path.Combine(request.SshdConfigDirectory, $"60-sshweave-{request.UserName}.conf");
        string config = TunnelUserPolicy.RenderSshdConfig(request.UserName);
        await EnsureRootAsync(cancellationToken);

        string nologin = File.Exists("/usr/sbin/nologin") ? "/usr/sbin/nologin" : "/sbin/nologin";
        ProcessResult account = await RunAsync("getent", ["passwd", request.UserName], cancellationToken);
        string homeDirectory;
        if (account.Succeeded)
        {
            string[] fields = account.StandardOutput.Trim().Split(':');
            if (fields.Length < 7 || !string.Equals(fields[6], nologin, StringComparison.Ordinal))
            {
                throw new ConfigurationException($"用户 {request.UserName} 已存在，但不是由 nologin 保护的专用通道用户。");
            }

            if (!File.Exists(configPath)
                || !string.Equals(
                    await File.ReadAllTextAsync(configPath, cancellationToken),
                    config,
                    StringComparison.Ordinal))
            {
                throw new ConfigurationException(
                    $"用户 {request.UserName} 已存在，但没有匹配的 SshWeave sshd 策略；为避免收编已有账户，安装已停止。");
            }

            homeDirectory = fields[5];
        }
        else
        {
            await RunRequiredAsync(
                "useradd",
                ["--create-home", "--user-group", "--shell", nologin, request.UserName],
                cancellationToken);
            account = await RunRequiredAsync("getent", ["passwd", request.UserName], cancellationToken);
            string[] fields = account.StandardOutput.Trim().Split(':');
            if (fields.Length < 7)
            {
                throw new ConfigurationException("创建用户后无法解析其 passwd 记录。");
            }

            homeDirectory = fields[5];
        }

        await InstallAuthorizedKeyAsync(request.UserName, homeDirectory, restrictedKey, cancellationToken);
        Directory.CreateDirectory(request.SshdConfigDirectory);
        byte[]? previousConfig = File.Exists(configPath)
            ? await File.ReadAllBytesAsync(configPath, cancellationToken)
            : null;

        await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), cancellationToken);
        File.SetUnixFileMode(
            configPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try
        {
            string sshd = File.Exists("/usr/sbin/sshd") ? "/usr/sbin/sshd" : "sshd";
            await RunRequiredAsync(sshd, ["-t"], cancellationToken);
            ProcessResult effective = await RunRequiredAsync(
                sshd,
                ["-T", "-C", $"user={request.UserName},host=localhost,addr=127.0.0.1"],
                cancellationToken);
            ValidateEffectiveSettings(effective.StandardOutput);

            bool reloaded = request.SkipReload || await TryReloadAsync(cancellationToken);
            return new ServerInstallResult(request.UserName, homeDirectory, configPath, reloaded && !request.SkipReload);
        }
        catch
        {
            // 配置验证失败时回滚 drop-in，避免下一次 sshd 重启采用半成品策略。
            if (previousConfig is null)
            {
                File.Delete(configPath);
            }
            else
            {
                await File.WriteAllBytesAsync(configPath, previousConfig, cancellationToken);
            }

            throw;
        }
    }

    private static async Task EnsureRootAsync(CancellationToken cancellationToken)
    {
        ProcessResult id = await RunRequiredAsync("id", ["-u"], cancellationToken);
        if (!int.TryParse(id.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int userId)
            || userId != 0)
        {
            throw new ConfigurationException("server-install 必须由 root 运行。");
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task InstallAuthorizedKeyAsync(
        string userName,
        string homeDirectory,
        string restrictedKey,
        CancellationToken cancellationToken)
    {
        string sshDirectory = Path.Combine(homeDirectory, ".ssh");
        string authorizedKeysPath = Path.Combine(sshDirectory, "authorized_keys");
        Directory.CreateDirectory(sshDirectory);

        if (File.Exists(authorizedKeysPath))
        {
            string[] existingKeys = (await File.ReadAllLinesAsync(authorizedKeysPath, cancellationToken))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (existingKeys.Any(line => !string.Equals(line.Trim(), restrictedKey, StringComparison.Ordinal)))
            {
                throw new ConfigurationException(
                    $"{authorizedKeysPath} 已包含其它密钥；为避免保留可获得 Shell 的入口，安装已停止。");
            }
        }

        await File.WriteAllTextAsync(
            authorizedKeysPath,
            restrictedKey + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken);
        File.SetUnixFileMode(
            sshDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(authorizedKeysPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await RunRequiredAsync("chown", [userName, sshDirectory], cancellationToken);
        await RunRequiredAsync("chown", [userName, authorizedKeysPath], cancellationToken);
    }

    private static void ValidateEffectiveSettings(string output)
    {
        HashSet<string> settings = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = RequiredEffectiveSettings.Where(setting => !settings.Contains(setting)).ToArray();
        if (missing.Length > 0)
        {
            throw new ConfigurationException(
                "sshd 有效配置未应用 SshWeave Match 策略；请确认主配置在首个 Match 前包含 sshd_config.d。缺少："
                + string.Join(", ", missing));
        }
    }

    private static async Task<bool> TryReloadAsync(CancellationToken cancellationToken)
    {
        string[][] attempts =
        [
            ["systemctl", "reload", "ssh"],
            ["systemctl", "reload", "sshd"],
            ["service", "ssh", "reload"],
            ["service", "sshd", "reload"],
        ];

        foreach (string[] attempt in attempts)
        {
            ProcessResult result = await RunAsync(attempt[0], attempt[1..], cancellationToken);
            if (result.Succeeded)
            {
                return true;
            }
        }

        throw new ConfigurationException("sshd 配置已通过校验，但服务重载失败；可使用 --no-reload 安装后手动重载。");
    }

    private static async Task<ProcessResult> RunRequiredAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(executable, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new ConfigurationException($"命令执行失败：{executable}（退出码 {result.ExitCode}）{Environment.NewLine}{detail}");
        }

        return result;
    }

    private static Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        ProcessExecutor.RunCapturedAsync(executable, arguments, cancellationToken);
}
