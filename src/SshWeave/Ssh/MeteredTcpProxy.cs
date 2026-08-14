using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SshWeave.Telemetry;

namespace SshWeave.Ssh;

internal sealed class MeteredTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _upstreamEndpoint;
    private readonly string _name;
    private readonly TrafficMetrics _metrics;
    private readonly Action<SessionLogEntry> _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private Task? _acceptTask;
    private long _nextConnectionId;

    public MeteredTcpProxy(
        IPEndPoint publicEndpoint,
        IPEndPoint upstreamEndpoint,
        string name,
        TrafficMetrics metrics,
        Action<SessionLogEntry> log)
    {
        _listener = new TcpListener(publicEndpoint);
        _upstreamEndpoint = upstreamEndpoint;
        _name = name;
        _metrics = metrics;
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptConnectionsAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
                // 停止监听时取消等待属于正常会话收尾。
            }
        }

        Task[] connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            await Task.WhenAll(connections);
        }

        _cancellation.Dispose();
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            long connectionId = Interlocked.Increment(ref _nextConnectionId);
            Task connection = RelayAsync(connectionId, client, cancellationToken);
            _connections[connectionId] = connection;
            _ = connection.ContinueWith(
                completedTask =>
                {
                    _ = completedTask;
                    _connections.TryRemove(connectionId, out Task? _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RelayAsync(long connectionId, TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (TcpClient upstream = new(_upstreamEndpoint.AddressFamily))
        {
            _metrics.ConnectionOpened();
            _log(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Network,
                $"{_name} 接受连接 #{connectionId}，来源 {client.Client.RemoteEndPoint}。"));
            try
            {
                await upstream.ConnectAsync(_upstreamEndpoint, cancellationToken);
                using NetworkStream clientStream = client.GetStream();
                using NetworkStream upstreamStream = upstream.GetStream();

                Task upload = CopyAndCountAsync(
                    clientStream,
                    upstreamStream,
                    _metrics.AddSent,
                    cancellationToken);
                Task download = CopyAndCountAsync(
                    upstreamStream,
                    clientStream,
                    _metrics.AddReceived,
                    cancellationToken);
                await Task.WhenAny(upload, download);

                // 一侧关闭后主动结束另一侧，避免半关闭连接永久占用统计名额。
                client.Close();
                upstream.Close();
                await IgnoreExpectedCloseAsync(upload);
                await IgnoreExpectedCloseAsync(download);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                _log(new SessionLogEntry(
                    DateTimeOffset.Now,
                    SessionLogCategory.Network,
                    $"{_name} 连接 #{connectionId} 中断：{exception.Message}",
                    IsError: true));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户停止会话时取消活动复制，不作为网络故障记录。
            }
            finally
            {
                _metrics.ConnectionClosed();
                _log(new SessionLogEntry(
                    DateTimeOffset.Now,
                    SessionLogCategory.Network,
                    $"{_name} 连接 #{connectionId} 已关闭。"));
            }
        }
    }

    private static async Task CopyAndCountAsync(
        Stream source,
        Stream destination,
        Action<int> count,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            count(read);
        }
    }

    private static async Task IgnoreExpectedCloseAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // 另一方向已经结束并关闭套接字时，当前复制任务会按平台抛出不同异常。
        }
    }
}
