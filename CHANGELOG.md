# 变更日志

## [未发布]

### 新增

- 对应路线图任务 `SW-5`，新增 Windows 透明 TCP 路由候选：配置可声明目标 IPv4 CIDR，CLI 与桌面会在 OpenSSH SOCKS5 就绪后启动固定哈希的 `tun2socks 2.6.0` 和 Wintun `0.14.1`，创建 `SshWeave` 虚拟网卡、写入仅当前启动有效的目标路由，并在任一进程退出时反向清理。实现包含默认路由/非规范 CIDR/地址重叠/既有路由冲突拒绝、管理员权限与依赖哈希预检、单网卡会话锁、桌面开关和计量链路；真实管理员网卡、`10.51.12.35:22` Xshell 直连、异常回滚及 Linux TUN 留待下一步验证，当前不标记完成。
- 对应路线图任务 `SW-8`，新增 MewUI `0.19.1` Windows/Direct2D 桌面控制台：管理连接账户及系统默认、密码、私钥文件认证，使用当前用户的一次性命名管道实现 `SSH_ASKPASS` 且不持久化秘密；可在界面启停 SOCKS5、TCP 映射和压缩，查看 OpenSSH 交互/网络日志、实时连接数与双向字节，并通过原生 `Shell_NotifyIcon` 托盘菜单打开、连接、断开和退出。新增原子配置保存、隐藏回环转发与本地计量代理；3 项目 Release 零警告、23 项测试和 6,976,000 字节 `win-x64` Native AOT 发布通过（SHA-256 `D94D8F270D6F8C0816DB92555DAB389AD357F1438E617B5BB5E958837C8220AE`），真实跳板机两类认证及托盘菜单人工验收仍待完成。
- 新增 .NET 10 Native AOT `sshweave` CLI、`sshweave.config.v1` 源生成 JSON 配置和跨平台发布脚本。
- 新增基于系统 OpenSSH 的 SOCKS5 动态转发及显式 TCP 本地映射，默认只监听回环地址。
- 新增 `run` 代理环境、SOCKS5 标准输入/输出桥接及 OpenSSH `ProxyCommand` 配置生成，可在活动通道内直接 SSH 到内网设备。
- 新增 Linux 专用通道用户安装器，通过 `nologin`、受限公钥和 `MaxSessions 0` 禁止 Shell，仅允许客户端 TCP 转发。
- 新增 17 项配置、SSH 参数、服务端策略、SOCKS5 编码和源生成 JSON 测试；完成 Windows/Linux x64 Native AOT 发布，并在隔离 Linux `sshd` 中验证 TCP 通道成功且 Shell/exec 被拒绝。

### 安全

- 强制使用 `strict` 或 `accept-new` 主机密钥策略，禁止关闭主机密钥校验。
- 非回环本地监听要求显式设置 `allowRemoteClients=true`，服务端策略默认关闭 `PermitTunnel`。
- 服务器配置在重载前通过 `sshd -t` 和匹配用户的 `sshd -T -C` 复核，失败时回滚 drop-in。
