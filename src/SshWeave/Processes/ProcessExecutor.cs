using System.Diagnostics;

namespace SshWeave.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public static class ProcessExecutor
{
    public static Task<ProcessResult> RunCapturedAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunCapturedAsync(executable, arguments, workingDirectory: null, cancellationToken);

    public static async Task<ProcessResult> RunCapturedAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable, arguments, workingDirectory);
        ConfigureBackgroundProcess(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{executable}");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    public static Process StartInteractive(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable, arguments, workingDirectory);
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动进程：{executable}");
        }

        return process;
    }

    public static Process StartObserved(
        string executable,
        IEnumerable<string> arguments,
        Action<string, bool> output,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable, arguments, workingDirectory);
        ConfigureBackgroundProcess(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output(eventArgs.Data, false);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output(eventArgs.Data, true);
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动进程：{executable}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出时无需继续清理。
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        foreach (string argument in arguments)
        {
            // ArgumentList 避免经由 shell 拼接，主机名和文件路径不会成为命令注入载体。
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ConfigureBackgroundProcess(ProcessStartInfo startInfo)
    {
        // 桌面端的 SSH 探测、PowerShell、netsh 和 TUN 辅助都在后台运行，输出由应用捕获。
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    }
}
