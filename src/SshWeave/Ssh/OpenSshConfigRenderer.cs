using System.Text;
using SshWeave.Configuration;

namespace SshWeave.Ssh;

public static class OpenSshConfigRenderer
{
    public static string RenderProxyCommand(
        string matchPattern,
        string sshWeaveExecutable,
        string configurationPath,
        SshProfile profile)
    {
        if (string.IsNullOrWhiteSpace(matchPattern)
            || matchPattern.Any(character => char.IsControl(character) || character is '\r' or '\n'))
        {
            throw new ConfigurationException("--match 必须是有效的 OpenSSH Host 匹配表达式。");
        }

        if (profile.Socks is null)
        {
            throw new ConfigurationException("生成 ProxyCommand 前必须为连接配置启用 socks。");
        }

        ValidateProxyCommandValue(sshWeaveExecutable, "--executable");
        ValidateProxyCommandValue(configurationPath, "--config");

        StringBuilder builder = new();
        builder.AppendLine($"Host {matchPattern}");
        builder.AppendLine(
            $"    ProxyCommand {Quote(sshWeaveExecutable)} socks-connect --config {Quote(Path.GetFullPath(configurationPath))} --profile {Quote(profile.Name)} %h %p");
        builder.AppendLine($"    ConnectTimeout {profile.ConnectTimeoutSeconds}");
        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static void ValidateProxyCommandValue(string value, string field)
    {
        const string unsafeCharacters = "\"%$`&|<>^!();\r\n\0";
        if (string.IsNullOrWhiteSpace(value) || value.Any(unsafeCharacters.Contains))
        {
            throw new ConfigurationException($"{field} 包含 ProxyCommand shell 无法安全引用的字符。");
        }
    }
}
