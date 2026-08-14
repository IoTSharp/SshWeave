namespace SshWeave.Telemetry;

public enum SessionLogCategory
{
    Lifecycle,
    Authentication,
    Network,
    OpenSsh,
}

public sealed record SessionLogEntry(
    DateTimeOffset Timestamp,
    SessionLogCategory Category,
    string Message,
    bool IsError = false);
