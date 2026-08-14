using Aprillz.MewUI;
using SshWeave.Security;

namespace SshWeave.Desktop.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? pipeName = Environment.GetEnvironmentVariable(AskPassBroker.PipeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            // OpenSSH 以当前可执行文件作为 SSH_ASKPASS；该分支只把一次性管道中的口令写到标准输出。
            string prompt = string.Join(' ', args);
            return AskPassBroker.RunClientAsync(pipeName, prompt).GetAwaiter().GetResult();
        }

        ThemeManager.DefaultAccent = Accent.Blue;
        using MainWindowController controller = new();
        Application.Create()
            .UseWin32()
            .UseDirect2D()
            .Run(controller.Window);
        return 0;
    }
}
