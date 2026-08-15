using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SshWeave.Configuration;
using SshWeave.Networking;
using SshWeave.Processes;

namespace SshWeave.Tun;

public sealed class TransparentTcpSession : IAsyncDisposable
{
    public const string SupportedTun2SocksVersion = "tun2socks-2.6.0";

    public const string SupportedWintunVersion = "0.14.1";
    internal const string WintunAmd64Sha256 = "E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE";
    internal const string WintunArm64Sha256 = "F7BA89005544BE9D85231A9E0D5F23B2D15B3311667E2DAD0DEBD344918A3F80";
    internal const string Tun2SocksAmd64Sha256 = "A1F8AC84852ED9A9C7A50949CD0290B6F1594E118BF053416B9B55B7CD7AE414";
    internal const string Tun2SocksArm64Sha256 = "1C04D2139D32C7A499C0F18E61EFC5C6043991217FACA13FF8B93C1222384263";

    private readonly Process _process;
    private readonly Task<int> _completion;
    private readonly Semaphore _adapterSemaphore;
    private readonly TransparentTcpRoute _route;
    private readonly List<Ipv4Cidr> _installedRoutes = [];
    private bool _addressConfigured;
    private int _stopped;

    private TransparentTcpSession(
        Process process,
        Semaphore adapterSemaphore,
        TransparentTcpRoute route)
    {
        _process = process;
        _completion = MonitorExitAsync(process);
        _adapterSemaphore = adapterSemaphore;
        _route = route;
    }

    public static bool IsEnabled(SshProfile profile) => profile.TransparentTcp?.Enabled == true;

    public static async Task CheckAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        bool requireElevation,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled(profile))
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new ConfigurationException("透明 TCP 路由当前只实现 Windows Wintun 数据面。");
        }
        if (requireElevation && !Environment.IsPrivilegedProcess)
        {
            throw new ConfigurationException("透明 TCP 路由需要管理员权限；请以管理员身份启动 SshWeave。");
        }

        string executable = ResolveTun2SocksExecutable(configuration);
        string workingDirectory = Path.GetDirectoryName(executable)!;
        string wintunPath = Path.Combine(workingDirectory, "wintun.dll");
        if (!File.Exists(executable))
        {
            throw new ConfigurationException($"找不到固定版本 tun2socks：{executable}");
        }
        if (!File.Exists(wintunPath))
        {
            throw new ConfigurationException($"找不到 Wintun 驱动入口：{wintunPath}");
        }

        (string expectedExecutableHash, string expectedWintunHash) = GetExpectedHashes();
        await VerifyHashAsync(executable, expectedExecutableHash, "tun2socks", cancellationToken);
        await VerifyHashAsync(wintunPath, expectedWintunHash, "wintun.dll", cancellationToken);

        ProcessResult version = await ProcessExecutor.RunCapturedAsync(
            executable,
            ["--version"],
            workingDirectory,
            cancellationToken);
        if (!version.Succeeded
            || !version.StandardOutput.StartsWith(SupportedTun2SocksVersion, StringComparison.Ordinal))
        {
            throw new ConfigurationException($"透明 TCP 数据面必须为 {SupportedTun2SocksVersion}。");
        }
    }

    public static async Task<TransparentTcpSession> StartAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        Action<string, bool>? output = null,
        CancellationToken cancellationToken = default)
    {
        await CheckAsync(configuration, profile, requireElevation: true, cancellationToken);
        TransparentTcpRoute route = profile.TransparentTcp;
        SocksForward socks = profile.Socks!;
        Semaphore adapterSemaphore = new(
            initialCount: 1,
            maximumCount: 1,
            $"Local\\SshWeave.Tun.{route.AdapterName}");
        bool lockTaken = adapterSemaphore.WaitOne(0);
        if (!lockTaken)
        {
            adapterSemaphore.Dispose();
            throw new ConfigurationException($"虚拟网卡 {route.AdapterName} 已由另一个 SshWeave 会话使用。");
        }

        Process? process = null;
        TransparentTcpSession? session = null;
        try
        {
            IReadOnlyList<Ipv4Cidr> destinations = ParseDestinations(route);
            await EnsureRoutesAvailableAsync(route, destinations, cancellationToken);

            string executable = ResolveTun2SocksExecutable(configuration);
            string workingDirectory = Path.GetDirectoryName(executable)!;
            process = ProcessExecutor.StartObserved(
                executable,
                WindowsTunCommandBuilder.BuildTun2Socks(route, socks),
                output ?? ((_, _) => { }),
                workingDirectory: workingDirectory);
            session = new TransparentTcpSession(process, adapterSemaphore, route);

            await WaitForAdapterAsync(process, route.AdapterName, profile.StartupTimeoutSeconds, cancellationToken);
            await session.RemoveStaleRoutesAsync(destinations, cancellationToken);
            await RunNetshAsync(WindowsTunCommandBuilder.BuildSetAddress(route), cancellationToken);
            session._addressConfigured = true;

            foreach (Ipv4Cidr destination in destinations)
            {
                await RunNetshAsync(
                    WindowsTunCommandBuilder.BuildAddRoute(route, destination),
                    cancellationToken);
                session._installedRoutes.Add(destination);
            }

            output?.Invoke(
                $"Wintun 网卡 {route.AdapterName} 已接管 {string.Join(", ", destinations)} 的 TCP 流量。",
                false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.StopAsync();
            }
            else
            {
                if (process is not null)
                {
                    ProcessExecutor.TryKill(process);
                    process.Dispose();
                }
                adapterSemaphore.Release();
                adapterSemaphore.Dispose();
            }
            throw;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return await _completion.WaitAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        // 先撤销目标路由，确保数据面退出期间不会留下指向失效网卡的黑洞。
        for (int index = _installedRoutes.Count - 1; index >= 0; index--)
        {
            await TryRunNetshAsync(WindowsTunCommandBuilder.BuildDeleteRoute(_route, _installedRoutes[index]));
        }
        _installedRoutes.Clear();

        ProcessExecutor.TryKill(_process);
        _ = await _completion;
        if (_addressConfigured)
        {
            await TryRunNetshAsync(WindowsTunCommandBuilder.BuildDeleteAddress(_route));
            _addressConfigured = false;
        }

        _process.Dispose();
        _adapterSemaphore.Release();
        _adapterSemaphore.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    internal static string ResolveTun2SocksExecutable(SshWeaveConfiguration configuration)
    {
        string path = configuration.Tun2SocksExecutable
            ?? Path.Combine(AppContext.BaseDirectory, "tun2socks.exe");
        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    internal static IReadOnlyList<string> ParseRouteAliases(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static Ipv4Cidr[] ParseDestinations(TransparentTcpRoute route) =>
        route.DestinationCidrs.Select(value =>
        {
            // 配置已通过同一解析器校验；这里只把通配表达式规范化为 netsh 使用的 CIDR。
            _ = Ipv4Cidr.TryParseRouteExpression(value, out Ipv4Cidr destination, out _);
            return destination;
        }).ToArray();

    private static async Task EnsureRoutesAvailableAsync(
        TransparentTcpRoute route,
        IReadOnlyList<Ipv4Cidr> destinations,
        CancellationToken cancellationToken)
    {
        foreach (Ipv4Cidr destination in destinations)
        {
            IReadOnlyList<string> aliases = await FindRouteAliasesAsync(destination, cancellationToken);
            string? conflict = aliases.FirstOrDefault(
                alias => !string.Equals(alias, route.AdapterName, StringComparison.OrdinalIgnoreCase));
            if (conflict is not null)
            {
                throw new ConfigurationException(
                    $"目标路由 {destination} 已由网卡 {conflict} 使用，SshWeave 不会覆盖现有路由。");
            }
        }
    }

    private async Task RemoveStaleRoutesAsync(
        IReadOnlyList<Ipv4Cidr> destinations,
        CancellationToken cancellationToken)
    {
        foreach (Ipv4Cidr destination in destinations)
        {
            IReadOnlyList<string> aliases = await FindRouteAliasesAsync(destination, cancellationToken);
            if (aliases.Any(alias => string.Equals(alias, _route.AdapterName, StringComparison.OrdinalIgnoreCase)))
            {
                await RunNetshAsync(WindowsTunCommandBuilder.BuildDeleteRoute(_route, destination), cancellationToken);
            }
        }
    }

    internal static async Task<IReadOnlyList<string>> FindRouteAliasesAsync(
        Ipv4Cidr destination,
        CancellationToken cancellationToken)
    {
        // Get-NetRoute 对不存在的 -DestinationPrefix 会静默返回退出码 1；先枚举再筛选可正确表达零匹配。
        string script =
            "$ErrorActionPreference = 'Stop'\n"
            + "Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | "
            + $"Where-Object {{ $_.DestinationPrefix -eq '{destination}' }} | "
            + "ForEach-Object { $_.InterfaceAlias }";
        ProcessResult result = await ProcessExecutor.RunCapturedAsync(
            GetWindowsPowerShellPath(),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new ConfigurationException($"无法检查现有 Windows 路由：{GetProcessError(result)}");
        }
        return ParseRouteAliases(result.StandardOutput);
    }

    private static async Task WaitForAdapterAsync(
        Process process,
        string adapterName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new ConfigurationException($"tun2socks 在 Wintun 网卡就绪前退出，退出码为 {process.ExitCode}。");
            }

            NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                item => string.Equals(item.Name, adapterName, StringComparison.OrdinalIgnoreCase));
            if (adapter?.OperationalStatus == OperationalStatus.Up)
            {
                return;
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new ConfigurationException($"等待 Wintun 网卡 {adapterName} 启动超时（{timeoutSeconds} 秒）。");
    }

    private static async Task RunNetshAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await ProcessExecutor.RunCapturedAsync(
            GetNetshPath(),
            arguments,
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new ConfigurationException($"Windows 网络配置失败：{GetProcessError(result)}");
        }
    }

    private static async Task TryRunNetshAsync(IReadOnlyList<string> arguments)
    {
        try
        {
            await RunNetshAsync(arguments);
        }
        catch (Exception exception) when (exception is ConfigurationException or IOException or InvalidOperationException)
        {
            // 清理阶段继续撤销剩余资源，避免单条已不存在的路由阻塞完整回滚。
        }
    }

    private static string ResolveSystemExecutable(params string[] parts)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrWhiteSpace(systemDirectory)
            ? parts[^1]
            : Path.Combine([systemDirectory, .. parts]);
    }

    private static string GetNetshPath() => ResolveSystemExecutable("netsh.exe");

    private static string GetWindowsPowerShellPath() =>
        ResolveSystemExecutable("WindowsPowerShell", "v1.0", "powershell.exe");

    private static async Task VerifyHashAsync(
        string path,
        string expected,
        string component,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        string actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ConfigurationException($"{component} 文件校验失败：{path}");
        }
    }

    private static (string Executable, string Wintun) GetExpectedHashes() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (Tun2SocksAmd64Sha256, WintunAmd64Sha256),
            Architecture.Arm64 => (Tun2SocksArm64Sha256, WintunArm64Sha256),
            _ => throw new ConfigurationException("透明 TCP 路由只支持 Windows x64 和 arm64。"),
        };

    private static string GetProcessError(ProcessResult result)
    {
        string error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrEmpty(error)
            ? $"进程退出码 {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : error;
    }

    private static async Task<int> MonitorExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
