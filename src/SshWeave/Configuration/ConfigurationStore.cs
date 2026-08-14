using System.Text;
using System.Text.Json;

namespace SshWeave.Configuration;

public static class ConfigurationStore
{
    public static string GetDefaultPath()
    {
        string baseDirectory;
        if (OperatingSystem.IsWindows())
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            baseDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(baseDirectory, "SshWeave", "config.json");
    }

    public static async Task<SshWeaveConfiguration> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"配置文件不存在：{path}");
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            SshWeaveConfiguration? configuration = await JsonSerializer.DeserializeAsync(
                stream,
                SshWeaveJsonContext.Default.SshWeaveConfiguration,
                cancellationToken);

            return configuration ?? throw new ConfigurationException("配置文件内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationException($"配置文件不是有效的 JSON：{exception.Message}");
        }
    }

    public static async Task WriteExampleAsync(
        string path,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(path) && !overwrite)
        {
            throw new ConfigurationException($"目标文件已存在：{path}。如需覆盖，请添加 --force。");
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 示例仅使用文档地址，不写入口令、私钥或真实网络端点。
        SshWeaveConfiguration example = new()
        {
            DefaultProfile = "station",
            Profiles =
            [
                new SshProfile
                {
                    Name = "station",
                    Host = "bastion.example.com",
                    User = "sshweave",
                    Socks = new SocksForward(),
                    TransparentTcp = new TransparentTcpRoute
                    {
                        DestinationCidrs = ["10.51.0.0/16"],
                    },
                    TcpForwards =
                    [
                        new TcpForward
                        {
                            LocalPort = 8443,
                            DestinationHost = "10.20.0.10",
                            DestinationPort = 443,
                        },
                    ],
                },
            ],
        };

        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(
            stream,
            example,
            SshWeaveJsonContext.Default.SshWeaveConfiguration,
            cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken);
    }

    public static async Task SaveAsync(
        string path,
        SshWeaveConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ConfigurationValidator.Validate(configuration);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // 同目录临时文件保证最终替换不跨卷，避免界面保存时留下半份 JSON。
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    configuration,
                    SshWeaveJsonContext.Default.SshWeaveConfiguration,
                    cancellationToken);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
