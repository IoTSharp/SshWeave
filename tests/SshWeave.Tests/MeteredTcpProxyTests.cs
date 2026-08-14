using System.Net;
using System.Net.Sockets;
using System.Text;
using SshWeave.Ssh;
using SshWeave.Telemetry;

namespace SshWeave.Tests;

public sealed class MeteredTcpProxyTests
{
    [Fact]
    public async Task ProxyCountsConnectionsAndBothTrafficDirections()
    {
        using TcpListener upstreamListener = new(IPAddress.Loopback, 0);
        upstreamListener.Start();
        int upstreamPort = ((IPEndPoint)upstreamListener.LocalEndpoint).Port;
        int publicPort = ReservePort();
        TrafficMetrics metrics = new();

        await using MeteredTcpProxy proxy = new(
            new IPEndPoint(IPAddress.Loopback, publicPort),
            new IPEndPoint(IPAddress.Loopback, upstreamPort),
            "test",
            metrics,
            _ => { });
        proxy.Start();

        Task upstream = Task.Run(async () =>
        {
            using TcpClient accepted = await upstreamListener.AcceptTcpClientAsync(
                TestContext.Current.CancellationToken);
            using NetworkStream stream = accepted.GetStream();
            byte[] request = new byte[4];
            await stream.ReadExactlyAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal("ping", Encoding.ASCII.GetString(request));
            await stream.WriteAsync("ack"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, publicPort, TestContext.Current.CancellationToken);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray(), TestContext.Current.CancellationToken);
            byte[] response = new byte[3];
            await stream.ReadExactlyAsync(response, TestContext.Current.CancellationToken);
            Assert.Equal("ack", Encoding.ASCII.GetString(response));
        }
        await upstream;

        TrafficSnapshot snapshot = await WaitForClosedSnapshotAsync(metrics);
        Assert.Equal(4, snapshot.BytesSent);
        Assert.Equal(3, snapshot.BytesReceived);
        Assert.Equal(0, snapshot.ActiveConnections);
        Assert.Equal(1, snapshot.TotalConnections);
    }

    private static int ReservePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<TrafficSnapshot> WaitForClosedSnapshotAsync(TrafficMetrics metrics)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            TrafficSnapshot snapshot = metrics.Snapshot();
            if (snapshot.TotalConnections == 1 && snapshot.ActiveConnections == 0)
            {
                return snapshot;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return metrics.Snapshot();
    }
}
