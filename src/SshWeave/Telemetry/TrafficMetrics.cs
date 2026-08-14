namespace SshWeave.Telemetry;

public sealed record TrafficSnapshot(
    long BytesSent,
    long BytesReceived,
    long ActiveConnections,
    long TotalConnections);

public sealed class TrafficMetrics
{
    private long _bytesSent;
    private long _bytesReceived;
    private long _activeConnections;
    private long _totalConnections;

    public TrafficSnapshot Snapshot() => new(
        Interlocked.Read(ref _bytesSent),
        Interlocked.Read(ref _bytesReceived),
        Interlocked.Read(ref _activeConnections),
        Interlocked.Read(ref _totalConnections));

    internal void ConnectionOpened()
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
    }

    internal void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    internal void AddSent(int count) => Interlocked.Add(ref _bytesSent, count);

    internal void AddReceived(int count) => Interlocked.Add(ref _bytesReceived, count);
}
