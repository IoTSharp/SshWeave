using System.Text;
using SshWeave.Configuration;

namespace SshWeave.Server;

public static class TunnelUserPolicy
{
    private static readonly string[] SupportedKeyPrefixes =
    [
        "ssh-ed25519 ",
        "sk-ssh-ed25519@openssh.com ",
        "ecdsa-sha2-nistp256 ",
        "ecdsa-sha2-nistp384 ",
        "ecdsa-sha2-nistp521 ",
        "sk-ecdsa-sha2-nistp256@openssh.com ",
        "ssh-rsa ",
    ];

    public static void ValidateUserName(string userName)
    {
        if (!ConfigurationValidator.UserNameRegex().IsMatch(userName))
        {
            throw new ConfigurationException("服务端用户名只能包含安全的 Unix 用户名字符，且最长为 64 个字符。");
        }
    }

    public static string RestrictPublicKey(string publicKey)
    {
        string[] lines = publicKey
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || !SupportedKeyPrefixes.Any(prefix => lines[0].StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new ConfigurationException("authorized key 文件必须只包含一条受支持的 OpenSSH 公钥，不能包含私钥或预置选项。");
        }

        string[] fields = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            throw new ConfigurationException("OpenSSH 公钥格式不完整。");
        }

        try
        {
            _ = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException)
        {
            throw new ConfigurationException("OpenSSH 公钥正文不是有效的 Base64。");
        }

        // restrict 关闭 PTY、命令、代理、X11 和用户 rc，再单独恢复端口转发能力。
        return $"restrict,port-forwarding {lines[0]}";
    }

    public static string RenderSshdConfig(string userName)
    {
        ValidateUserName(userName);
        StringBuilder builder = new();
        builder.AppendLine("# 由 SshWeave 管理：仅允许客户端发起 TCP 通道，禁止 Shell 和三层隧道。");
        builder.AppendLine($"Match User {userName}");
        builder.AppendLine("    AuthenticationMethods publickey");
        builder.AppendLine("    PubkeyAuthentication yes");
        builder.AppendLine("    PasswordAuthentication no");
        builder.AppendLine("    KbdInteractiveAuthentication no");
        builder.AppendLine("    AllowAgentForwarding no");
        builder.AppendLine("    AllowTcpForwarding local");
        builder.AppendLine("    AllowStreamLocalForwarding no");
        builder.AppendLine("    GatewayPorts no");
        builder.AppendLine("    PermitOpen any");
        builder.AppendLine("    PermitTTY no");
        builder.AppendLine("    PermitTunnel no");
        builder.AppendLine("    PermitUserRC no");
        builder.AppendLine("    X11Forwarding no");
        builder.AppendLine("    MaxSessions 0");
        return builder.ToString();
    }
}
