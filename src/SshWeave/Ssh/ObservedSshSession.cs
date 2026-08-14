using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SshWeave.Configuration;
using SshWeave.Processes;
using SshWeave.Security;
using SshWeave.Telemetry;
using SshWeave.Tun;

namespace SshWeave.Ssh;

public enum ObservedSessionState
{
    Starting,
    Connected,
    Stopping,
    Stopped,
    Failed,
}

public sealed class ObservedSshSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<MeteredTcpProxy> _proxies;
    private readonly ConcurrentQueue<SessionLogEntry> _logEntries = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private TransparentTcpSession? _transparentTcp;
    private Task? _monitorTask;
    private ObservedSessionState _state = ObservedSessionState.Starting;

    private ObservedSshSession(Process process, List<MeteredTcpProxy> proxies)
    {
        _process = process;
        _proxies = proxies;
    }

    public event Action<SessionLogEntry>? LogReceived;

    public event Action<ObservedSessionState>? StateChanged;

    public TrafficMetrics Metrics { get; } = new();

    public IReadOnlyList<SessionLogEntry> LogEntries => _logEntries.ToArray();

    public ObservedSessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public static async Task<ObservedSshSession> StartAsync(
        SshWeaveConfiguration configuration,
        SshProfile profile,
        string askPassExecutable,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ConfigurationValidator.Validate(configuration);
        SshConnectionService.ValidateLocalFiles(profile);
        LocalEndpointProbe.EnsureAvailable(profile);

        RuntimePlan plan = CreateRuntimePlan(profile);
        AskPassBroker? broker = string.IsNullOrEmpty(secret) ? null : new AskPassBroker(secret);
        Process? process = null;
        ObservedSshSession? session = null;
        try
        {
            IReadOnlyDictionary<string, string?>? environment = broker?.CreateEnvironment(askPassExecutable);
            List<SessionLogEntry> startupLog = [];
            process = ProcessExecutor.StartObserved(
                configuration.SshExecutable,
                SshArgumentBuilder.Build(profile, runtimeForwards: plan.Forwards, verbose: true),
                (line, isError) =>
                {
                    SessionLogEntry entry = CreateOpenSshLog(line, isError);
                    lock (startupLog)
                    {
                        if (session is null)
                        {
                            startupLog.Add(entry);
                        }
                        else
                        {
                            session.PublishLog(entry);
                        }
                    }
                },
                environment);

            List<MeteredTcpProxy> proxies = [];
            session = new ObservedSshSession(process, proxies);
            foreach (ProxyBinding binding in plan.Bindings)
            {
                proxies.Add(new MeteredTcpProxy(
                    binding.PublicEndpoint,
                    binding.HiddenEndpoint,
                    binding.Name,
                    session.Metrics,
                    session.PublishLog));
            }

            lock (startupLog)
            {
                foreach (SessionLogEntry entry in startupLog)
                {
                    session.PublishLog(entry);
                }
                startupLog.Clear();
            }

            session.PublishLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Lifecycle,
                $"正在连接 {profile.User}@{profile.Host}:{profile.Port}。"));
            await WaitForHiddenListenersAsync(
                process,
                plan.Bindings.Select(binding => binding.HiddenEndpoint.Port).ToArray(),
                profile.StartupTimeoutSeconds,
                cancellationToken);

            foreach (MeteredTcpProxy proxy in proxies)
            {
                proxy.Start();
            }

            if (TransparentTcpSession.IsEnabled(profile))
            {
                session._transparentTcp = await TransparentTcpSession.StartAsync(
                    configuration,
                    profile,
                    (line, isError) => session.PublishLog(new SessionLogEntry(
                        DateTimeOffset.Now,
                        SessionLogCategory.Network,
                        $"TUN: {line}",
                        isError)),
                    cancellationToken);
            }

            session.SetState(ObservedSessionState.Connected);
            session.PublishLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Lifecycle,
                "SSH 通道及本地计量入口已就绪。"));
            session._monitorTask = session.MonitorProcessAsync();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.SetState(ObservedSessionState.Failed);
                if (session._transparentTcp is not null)
                {
                    await session._transparentTcp.DisposeAsync();
                }
                foreach (MeteredTcpProxy proxy in session._proxies)
                {
                    await proxy.DisposeAsync();
                }
            }
            if (process is not null)
            {
                ProcessExecutor.TryKill(process);
                process.Dispose();
            }
            throw;
        }
        finally
        {
            if (broker is not null)
            {
                await broker.DisposeAsync();
            }
        }
    }

    public async Task StopAsync()
    {
        if (State is ObservedSessionState.Stopped or ObservedSessionState.Stopping)
        {
            return;
        }

        SetState(ObservedSessionState.Stopping);
        await _lifetime.CancelAsync();
        if (_transparentTcp is not null)
        {
            await _transparentTcp.DisposeAsync();
            _transparentTcp = null;
        }
        foreach (MeteredTcpProxy proxy in _proxies)
        {
            await proxy.DisposeAsync();
        }

        ProcessExecutor.TryKill(_process);
        if (_monitorTask is not null)
        {
            await _monitorTask;
        }
        SetState(ObservedSessionState.Stopped);
        PublishLog(new SessionLogEntry(
            DateTimeOffset.Now,
            SessionLogCategory.Lifecycle,
            "SSH 通道已停止。"));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process.Dispose();
        _lifetime.Dispose();
    }

    private static RuntimePlan CreateRuntimePlan(SshProfile profile)
    {
        HashSet<int> allocatedPorts = [];
        List<ProxyBinding> bindings = [];
        SocksForward? hiddenSocks = null;
        if (profile.Socks is not null)
        {
            int port = AllocateHiddenPort(allocatedPorts);
            hiddenSocks = new SocksForward { ListenAddress = "127.0.0.1", Port = port };
            bindings.Add(new ProxyBinding(
                "SOCKS5",
                new IPEndPoint(IPAddress.Parse(profile.Socks.ListenAddress), profile.Socks.Port),
                new IPEndPoint(IPAddress.Loopback, port)));
        }

        List<TcpForward> hiddenForwards = [];
        for (int index = 0; index < profile.TcpForwards.Count; index++)
        {
            TcpForward forward = profile.TcpForwards[index];
            int port = AllocateHiddenPort(allocatedPorts);
            hiddenForwards.Add(new TcpForward
            {
                ListenAddress = "127.0.0.1",
                LocalPort = port,
                DestinationHost = forward.DestinationHost,
                DestinationPort = forward.DestinationPort,
            });
            bindings.Add(new ProxyBinding(
                $"TCP {forward.DestinationHost}:{forward.DestinationPort}",
                new IPEndPoint(IPAddress.Parse(forward.ListenAddress), forward.LocalPort),
                new IPEndPoint(IPAddress.Loopback, port)));
        }

        return new RuntimePlan(new SshRuntimeForwardPlan(hiddenSocks, hiddenForwards), bindings);
    }

    private static int AllocateHiddenPort(HashSet<int> allocatedPorts)
    {
        while (true)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (allocatedPorts.Add(port))
            {
                return port;
            }
        }
    }

    private static async Task WaitForHiddenListenersAsync(
        Process process,
        IReadOnlyCollection<int> ports,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new ConfigurationException($"SSH 在本地通道就绪前退出，退出码为 {process.ExitCode}。");
            }

            HashSet<int> listeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
            if (ports.All(listeners.Contains))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new ConfigurationException($"等待 OpenSSH 本地通道启动超时（{timeoutSeconds} 秒）。");
    }

    private async Task MonitorProcessAsync()
    {
        Task sshExit = _process.WaitForExitAsync();
        Task<int>? tunExit = _transparentTcp?.WaitForExitAsync();
        Task firstExit = tunExit is null ? sshExit : await Task.WhenAny(sshExit, tunExit);
        if (tunExit is not null && firstExit == tunExit && State == ObservedSessionState.Connected)
        {
            int exitCode = await tunExit;
            await _transparentTcp!.StopAsync();
            ProcessExecutor.TryKill(_process);
            SetState(ObservedSessionState.Failed);
            PublishLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Network,
                $"透明 TCP 数据面意外退出，退出码 {exitCode}。",
                IsError: true));
            return;
        }

        await sshExit;
        if (State == ObservedSessionState.Connected)
        {
            if (_transparentTcp is not null)
            {
                await _transparentTcp.DisposeAsync();
                _transparentTcp = null;
            }
            foreach (MeteredTcpProxy proxy in _proxies)
            {
                await proxy.DisposeAsync();
            }
            SetState(_process.ExitCode == 0 ? ObservedSessionState.Stopped : ObservedSessionState.Failed);
            PublishLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Lifecycle,
                $"SSH 进程已退出，退出码 {_process.ExitCode}。",
                IsError: _process.ExitCode != 0));
        }
    }

    private static SessionLogEntry CreateOpenSshLog(string line, bool isError)
    {
        SessionLogCategory category = line.StartsWith("debug", StringComparison.OrdinalIgnoreCase)
            ? SessionLogCategory.Network
            : SessionLogCategory.OpenSsh;
        return new SessionLogEntry(DateTimeOffset.Now, category, line, isError && category != SessionLogCategory.Network);
    }

    private void PublishLog(SessionLogEntry entry)
    {
        _logEntries.Enqueue(entry);
        while (_logEntries.Count > 1000)
        {
            _logEntries.TryDequeue(out _);
        }
        LogReceived?.Invoke(entry);
    }

    private void SetState(ObservedSessionState state)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    private sealed record ProxyBinding(string Name, IPEndPoint PublicEndpoint, IPEndPoint HiddenEndpoint);

    private sealed record RuntimePlan(SshRuntimeForwardPlan Forwards, List<ProxyBinding> Bindings);
}
