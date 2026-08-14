using System.IO.Pipes;
using System.Text;

namespace SshWeave.Security;

public sealed class AskPassBroker : IAsyncDisposable
{
    public const string PipeEnvironmentVariable = "SSHWEAVE_ASKPASS_PIPE";

    private readonly string _secret;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    public AskPassBroker(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("认证口令不能为空。", nameof(secret));
        }

        _secret = secret;
        PipeName = $"sshweave-askpass-{Guid.NewGuid():N}";
        _serverTask = ServeAsync(_cancellation.Token);
    }

    public string PipeName { get; }

    public IReadOnlyDictionary<string, string?> CreateEnvironment(string askPassExecutable)
    {
        string fullPath = Path.GetFullPath(askPassExecutable);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SSH_ASKPASS"] = fullPath,
            ["SSH_ASKPASS_REQUIRE"] = "force",
            [PipeEnvironmentVariable] = PipeName,
            // 部分 OpenSSH 构建仍以 DISPLAY 是否存在决定是否调用 askpass。
            ["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? "sshweave:0",
        };
    }

    public static async Task<int> RunClientAsync(
        string pipeName,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000, cancellationToken);

        using StreamWriter writer = new(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using StreamReader reader = new(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(prompt.AsMemory(), cancellationToken);
        string? secret = await reader.ReadLineAsync(cancellationToken);
        if (secret is null)
        {
            return 1;
        }

        Console.Out.Write(secret);
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
            // 会话完成后关闭等待中的一次性认证管道。
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream server = new(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken);

            using StreamReader reader = new(server, Encoding.UTF8, leaveOpen: true);
            using StreamWriter writer = new(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _ = await reader.ReadLineAsync(cancellationToken);
            await writer.WriteLineAsync(_secret.AsMemory(), cancellationToken);
        }
    }
}
