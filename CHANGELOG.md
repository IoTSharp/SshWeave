# 变更日志

## [未发布]

### 新增

- 新增 .NET 10 Native AOT `sshweave` CLI、`sshweave.config.v1` 源生成 JSON 配置和跨平台发布脚本。
- 新增基于系统 OpenSSH 的 SOCKS5 动态转发及显式 TCP 本地映射，默认只监听回环地址。
- 新增 `run` 代理环境、SOCKS5 标准输入/输出桥接及 OpenSSH `ProxyCommand` 配置生成，可在活动通道内直接 SSH 到内网设备。
- 新增 Linux 专用通道用户安装器，通过 `nologin`、受限公钥和 `MaxSessions 0` 禁止 Shell，仅允许客户端 TCP 转发。
- 新增 17 项配置、SSH 参数、服务端策略、SOCKS5 编码和源生成 JSON 测试；完成 Windows/Linux x64 Native AOT 发布，并在隔离 Linux `sshd` 中验证 TCP 通道成功且 Shell/exec 被拒绝。

### 安全

- 强制使用 `strict` 或 `accept-new` 主机密钥策略，禁止关闭主机密钥校验。
- 非回环本地监听要求显式设置 `allowRemoteClients=true`，服务端策略默认关闭 `PermitTunnel`。
- 服务器配置在重载前通过 `sshd -t` 和匹配用户的 `sshd -T -C` 复核，失败时回滚 drop-in。
