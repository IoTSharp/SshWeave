using System.Globalization;
using SshWeave.Cli;
using SshWeave.Configuration;
using SshWeave.Server;
using SshWeave.Ssh;

namespace SshWeave;

internal static class Program
{
    private const string Version = "0.1.0";

    private static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await DispatchAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (ConfigurationException exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"失败：{exception.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        string command = args[0];
        string[] commandArguments = args[1..];
        return command switch
        {
            "version" or "--version" => PrintVersion(),
            "init" => await InitializeAsync(commandArguments, cancellationToken),
            "check" => await CheckAsync(commandArguments, cancellationToken),
            "connect" => await ConnectAsync(commandArguments, cancellationToken),
            "run" => await RunChildAsync(commandArguments, cancellationToken),
            "socks-connect" => await SocksConnectAsync(commandArguments, cancellationToken),
            "ssh-config" => await PrintOpenSshConfigAsync(commandArguments, cancellationToken),
            "server-config" => PrintServerConfig(commandArguments),
            "server-install" => await InstallServerUserAsync(commandArguments, cancellationToken),
            _ => throw new ConfigurationException($"未知命令：{command}。运行 sshweave help 查看用法。"),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"SshWeave {Version}");
        return 0;
    }

    private static async Task<int> InitializeAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, ["--output"], ["--force"]);
        EnsureNoPositionals(parsed);
        string output = parsed.GetValue("--output") ?? ConfigurationStore.GetDefaultPath();
        await ConfigurationStore.WriteExampleAsync(output, parsed.HasFlag("--force"), cancellationToken);
        Console.WriteLine($"已创建示例配置：{Path.GetFullPath(output)}");
        return 0;
    }

    private static async Task<int> CheckAsync(string[] args, CancellationToken cancellationToken)
    {
        (SshWeaveConfiguration configuration, SshProfile profile, _) =
            await LoadProfileAsync(args, cancellationToken);
        await SshConnectionService.CheckAsync(configuration, profile, cancellationToken);
        Console.WriteLine($"配置 {profile.Name} 已通过本地端点、文件和 OpenSSH 参数检查。");
        Console.WriteLine(SshArgumentBuilder.FormatForDisplay(
            configuration.SshExecutable,
            SshArgumentBuilder.Build(profile)));
        return 0;
    }

    private static async Task<int> ConnectAsync(string[] args, CancellationToken cancellationToken)
    {
        (SshWeaveConfiguration configuration, SshProfile profile, CliArguments parsed) =
            await LoadProfileAsync(args, cancellationToken, ["--dry-run"]);
        IReadOnlyList<string> sshArguments = SshArgumentBuilder.Build(profile);
        if (parsed.HasFlag("--dry-run"))
        {
            Console.WriteLine(SshArgumentBuilder.FormatForDisplay(configuration.SshExecutable, sshArguments));
            return 0;
        }

        Console.WriteLine($"正在连接 {profile.User}@{profile.Host}:{profile.Port}；按 Ctrl+C 关闭全部本地通道。");
        if (profile.Socks is not null)
        {
            Console.WriteLine($"SOCKS5：{profile.Socks.ListenAddress}:{profile.Socks.Port}");
        }

        foreach (TcpForward forward in profile.TcpForwards)
        {
            Console.WriteLine(
                $"TCP：{forward.ListenAddress}:{forward.LocalPort} -> {forward.DestinationHost}:{forward.DestinationPort}");
        }

        return await SshConnectionService.ConnectAsync(configuration, profile, cancellationToken);
    }

    private static async Task<int> RunChildAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, ["--config", "--profile"], allowTail: true);
        EnsureNoPositionals(parsed);
        if (parsed.Tail.Count == 0)
        {
            throw new ConfigurationException("run 用法：sshweave run [选项] -- <程序> [参数...]。");
        }

        (SshWeaveConfiguration configuration, SshProfile profile) = await LoadProfileFromParsedAsync(
            parsed,
            cancellationToken);
        return await SshConnectionService.RunAsync(
            configuration,
            profile,
            parsed.Tail[0],
            parsed.Tail.Skip(1).ToArray(),
            cancellationToken);
    }

    private static async Task<int> SocksConnectAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, ["--config", "--profile"]);
        if (parsed.Positionals.Count != 2
            || !int.TryParse(parsed.Positionals[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            throw new ConfigurationException(
                "socks-connect 用法：sshweave socks-connect [选项] <目标主机> <目标端口>。");
        }

        (_, SshProfile profile) = await LoadProfileFromParsedAsync(parsed, cancellationToken);
        if (profile.Socks is null)
        {
            throw new ConfigurationException("socks-connect 要求连接配置启用 socks。");
        }

        await Socks5Bridge.RunAsync(profile.Socks, parsed.Positionals[0], port, cancellationToken);
        return 0;
    }

    private static async Task<int> PrintOpenSshConfigAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(
            args,
            ["--config", "--profile", "--match", "--executable"]);
        EnsureNoPositionals(parsed);
        string configurationPath = parsed.GetValue("--config") ?? ConfigurationStore.GetDefaultPath();
        (SshWeaveConfiguration _, SshProfile profile) = await LoadProfileFromParsedAsync(parsed, cancellationToken);
        string match = parsed.GetValue("--match")
            ?? throw new ConfigurationException("ssh-config 需要 --match，例如 --match \"10.* 192.168.*\"。");
        string executable = parsed.GetValue("--executable") ?? "sshweave";
        Console.Write(OpenSshConfigRenderer.RenderProxyCommand(match, executable, configurationPath, profile));
        return 0;
    }

    private static int PrintServerConfig(string[] args)
    {
        CliArguments parsed = CliArguments.Parse(args, ["--user"]);
        EnsureNoPositionals(parsed);
        string user = parsed.GetValue("--user") ?? "sshweave";
        Console.Write(TunnelUserPolicy.RenderSshdConfig(user));
        return 0;
    }

    private static async Task<int> InstallServerUserAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(
            args,
            ["--user", "--authorized-key", "--sshd-config-dir"],
            ["--no-reload"]);
        EnsureNoPositionals(parsed);
        string user = parsed.GetValue("--user") ?? "sshweave";
        string authorizedKey = parsed.GetValue("--authorized-key")
            ?? throw new ConfigurationException("server-install 需要 --authorized-key 指向一条 OpenSSH 公钥。");
        string configDirectory = parsed.GetValue("--sshd-config-dir") ?? "/etc/ssh/sshd_config.d";
        ServerInstallResult result = await ServerInstaller.InstallAsync(
            new ServerInstallRequest(user, authorizedKey, configDirectory, parsed.HasFlag("--no-reload")),
            cancellationToken);
        Console.WriteLine($"通道用户已就绪：{result.UserName}");
        Console.WriteLine($"主目录：{result.HomeDirectory}");
        Console.WriteLine($"sshd 策略：{result.SshdConfigPath}");
        Console.WriteLine(result.Reloaded ? "sshd 已重载。" : "已按 --no-reload 跳过 sshd 重载，请人工完成。");
        return 0;
    }

    private static async Task<(SshWeaveConfiguration Configuration, SshProfile Profile, CliArguments Parsed)>
        LoadProfileAsync(
            string[] args,
            CancellationToken cancellationToken,
            IEnumerable<string>? extraFlags = null)
    {
        CliArguments parsed = CliArguments.Parse(args, ["--config", "--profile"], extraFlags);
        EnsureNoPositionals(parsed);
        (SshWeaveConfiguration configuration, SshProfile profile) = await LoadProfileFromParsedAsync(
            parsed,
            cancellationToken);
        return (configuration, profile, parsed);
    }

    private static async Task<(SshWeaveConfiguration Configuration, SshProfile Profile)> LoadProfileFromParsedAsync(
        CliArguments parsed,
        CancellationToken cancellationToken)
    {
        string path = parsed.GetValue("--config") ?? ConfigurationStore.GetDefaultPath();
        SshWeaveConfiguration configuration = await ConfigurationStore.LoadAsync(path, cancellationToken);
        SshProfile profile = ConfigurationValidator.ResolveProfile(configuration, parsed.GetValue("--profile"));
        return (configuration, profile);
    }

    private static void EnsureNoPositionals(CliArguments parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new ConfigurationException($"不支持的位置参数：{string.Join(' ', parsed.Positionals)}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            SshWeave 0.1.0 - 通过标准 OpenSSH 复用远端网络可达性

            用法：
              sshweave init [--output <文件>] [--force]
              sshweave check [--config <文件>] [--profile <名称>]
              sshweave connect [--config <文件>] [--profile <名称>] [--dry-run]
              sshweave run [--config <文件>] [--profile <名称>] -- <程序> [参数...]
              sshweave ssh-config --match <Host模式> [--config <文件>] [--profile <名称>]
              sshweave server-config [--user <用户名>]
              sshweave server-install --authorized-key <公钥文件> [--user <用户名>] [--no-reload]

            能力边界：
              默认模式支持 SOCKS5 和任意 TCP 端口映射，包括 HTTP、HTTPS、SSH 和数据库。
              标准 SSH 端口转发不承载 ICMP 或通用 UDP；真实 ping/UDP 需要 PermitTunnel 和回程路由或 NAT。
            """);
    }
}
