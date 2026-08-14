using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SshWeave.Configuration;
using SshWeave.Processes;

namespace SshWeave.Ssh;

public static class SshConnectionService
{
    public static async Task CheckAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalFiles(profile);
        LocalEndpointProbe.EnsureAvailable(profile);

        ProcessResult version = await ProcessExecutor.RunCapturedAsync(
            configuration.SshExecutable,
            ["-V"],
            cancellationToken);
        if (!version.Succeeded)
        {
            throw new ConfigurationException($"OpenSSH 客户端不可用：{version.StandardError.Trim()}");
        }

        ProcessResult parsed = await ProcessExecutor.RunCapturedAsync(
            configuration.SshExecutable,
            SshArgumentBuilder.Build(profile, configurationDump: true),
            cancellationToken);
        if (!parsed.Succeeded)
        {
            string detail = string.IsNullOrWhiteSpace(parsed.StandardError)
                ? parsed.StandardOutput.Trim()
                : parsed.StandardError.Trim();
            throw new ConfigurationException($"OpenSSH 拒绝连接配置：{detail}");
        }
    }

    public static async Task<int> ConnectAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalFiles(profile);
        LocalEndpointProbe.EnsureAvailable(profile);

        using Process process = ProcessExecutor.StartInteractive(
            configuration.SshExecutable,
            SshArgumentBuilder.Build(profile));
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            ProcessExecutor.TryKill(process);
            return 130;
        }
    }

    public static async Task<int> RunAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (profile.Socks is null)
        {
            throw new ConfigurationException("run 命令需要配置 socks 监听端点。");
        }

        ValidateLocalFiles(profile);
        LocalEndpointProbe.EnsureAvailable(profile);
        using Process ssh = ProcessExecutor.StartInteractive(
            configuration.SshExecutable,
            SshArgumentBuilder.Build(profile));

        try
        {
            await WaitForSocksAsync(ssh, profile.Socks, profile.StartupTimeoutSeconds, cancellationToken);
            string proxyUrl = BuildProxyUrl(profile.Socks);
            Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ALL_PROXY"] = proxyUrl,
                ["all_proxy"] = proxyUrl,
                ["HTTP_PROXY"] = proxyUrl,
                ["http_proxy"] = proxyUrl,
                ["HTTPS_PROXY"] = proxyUrl,
                ["https_proxy"] = proxyUrl,
            };

            using Process child = ProcessExecutor.StartInteractive(executable, arguments, environment);
            try
            {
                Task childExit = child.WaitForExitAsync(cancellationToken);
                Task sshExit = ssh.WaitForExitAsync(cancellationToken);
                Task firstExit = await Task.WhenAny(childExit, sshExit);
                if (firstExit == sshExit)
                {
                    await sshExit;
                    ProcessExecutor.TryKill(child);
                    throw new ConfigurationException($"SSH 通道在子程序退出前中断，退出码为 {ssh.ExitCode}。");
                }

                await childExit;
                return child.ExitCode;
            }
            catch (OperationCanceledException)
            {
                ProcessExecutor.TryKill(child);
                return 130;
            }
        }
        finally
        {
            ProcessExecutor.TryKill(ssh);
        }
    }

    private static void ValidateLocalFiles(SshProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.IdentityFile) && !File.Exists(profile.IdentityFile))
        {
            throw new ConfigurationException($"私钥文件不存在：{profile.IdentityFile}");
        }

        if (!string.IsNullOrWhiteSpace(profile.KnownHostsFile))
        {
            string fullPath = Path.GetFullPath(profile.KnownHostsFile);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is not null && !Directory.Exists(directory))
            {
                throw new ConfigurationException($"known_hosts 目录不存在：{directory}");
            }

            if (profile.HostKeyPolicy == HostKeyPolicies.Strict && !File.Exists(fullPath))
            {
                throw new ConfigurationException($"strict 模式要求 known_hosts 文件已存在：{fullPath}");
            }
        }
    }

    private static async Task WaitForSocksAsync(
        Process ssh,
        SocksForward socks,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        IPAddress listenAddress = IPAddress.Parse(socks.ListenAddress);
        IPAddress probeAddress = listenAddress.Equals(IPAddress.Any)
            ? IPAddress.Loopback
            : listenAddress.Equals(IPAddress.IPv6Any)
                ? IPAddress.IPv6Loopback
                : listenAddress;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ssh.HasExited)
            {
                throw new ConfigurationException($"SSH 在代理就绪前退出，退出码为 {ssh.ExitCode}。");
            }

            try
            {
                using TcpClient client = new(probeAddress.AddressFamily);
                await client.ConnectAsync(probeAddress, socks.Port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        throw new ConfigurationException($"等待 SOCKS5 代理启动超时（{timeoutSeconds} 秒）。");
    }

    private static string BuildProxyUrl(SocksForward socks)
    {
        IPAddress listenAddress = IPAddress.Parse(socks.ListenAddress);
        string host = listenAddress.Equals(IPAddress.Any)
            ? "127.0.0.1"
            : listenAddress.Equals(IPAddress.IPv6Any)
                ? "[::1]"
                : listenAddress.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{listenAddress}]"
                    : listenAddress.ToString();
        return $"socks5h://{host}:{socks.Port}";
    }
}
