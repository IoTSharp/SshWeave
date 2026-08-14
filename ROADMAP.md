# SshWeave 路线图

状态：✅ 已完成 · 🚧 进行中 · ⏳ 待处理 · ⚠️ 已阻塞 · 🧪 验证中

| 编号 | 状态 | 任务 | 验收证据 |
| --- | --- | --- | --- |
| SW-1 | ✅ | 建立 .NET 10 Native AOT CLI、V1 配置和系统 OpenSSH 边界 | Release 零警告构建、17 项测试、`ssh -G` 参数检查及 Windows/Linux x64 Native AOT 发布通过 |
| SW-2 | ✅ | 提供 SOCKS5、显式 TCP 映射和进程代理环境 | 参数构建、回环限制、源生成 JSON 和命令冒烟通过 |
| SW-3 | ✅ | 支持活动通道内直接 SSH 到内网设备 | SOCKS5 标准输入/输出桥接与 `ProxyCommand` 生成测试通过 |
| SW-4 | ✅ | 创建只能转发、不能 Shell 的服务端专用用户 | 隔离 Linux `sshd` 验证 `-N -L` 成功且 exec 被拒绝；`nologin`、受限公钥、`MaxSessions 0`、有效配置复核和失败回滚已实现 |
| SW-5 | 🧪 | 使用经过审查的本地 TUN 方案实现透明 TCP 路由 | Windows 已实现固定哈希的 `tun2socks 2.6.0`/Wintun `0.14.1` 数据面、CIDR 安全校验、活动路由写入、进程联动清理及 CLI/桌面入口；待管理员实机与 Linux 实现验收 |
| SW-6 | ⚠️ | 提供真实 ICMP 和通用 UDP 的可选 L3 模式 | 依赖 `PermitTunnel` 及远端返回路由或限定 NAT；未获得服务器变更验收前不得启用 |
| SW-7 | ⏳ | 增加安装包、代码签名、发行校验和端到端 SSH 测试 | Windows/Linux 发布资产和真实双主机测试通过 |
| SW-8 | 🧪 | 使用 MewUI 实现 Windows Native AOT 桌面控制台、托盘、认证与可观测会话 | 3 项目 Release 零警告、23 项测试及 6,976,000 字节 `win-x64` Native AOT 发布通过（SHA-256 `D94D8F270D6F8C0816DB92555DAB389AD357F1438E617B5BB5E958837C8220AE`）；窗口启动、关闭后托盘驻留、账户/功能管理、日志、连接数和双向字节计量已验证，仍待真实跳板机密码及受口令私钥登录和托盘右键人工验收 |

## SW-5 下一步验证

1. 以 `win-x64` 发布脚本生成候选，复核 `sshweave`、`tun2socks.exe`、`wintun.dll`、
   `THIRD-PARTY-NOTICES.md` 和 `WINTUN-LICENSE.txt` 的文件清单与 SHA-256。
2. 以管理员身份启用 `10.51.0.0/16`，确认 `SshWeave` Wintun 网卡使用
   `198.18.0.1/30`，且只新增目标 CIDR 的活动路由，不修改默认路由、远端路由或 NAT。
3. 保持 SSH 会话后使用 Xshell 直接连接 `10.51.12.35:22`，记录握手、认证、连接数及双向
   字节证据；不得设置 SOCKS5 或 `ProxyCommand`。
4. 主动断开、SSH 异常退出和 `tun2socks` 异常退出各执行一次，确认目标路由与 TUN 地址均被
   清理；再验证现有同前缀路由会默认拒绝覆盖，进程重启可清理同名网卡上的遗留活动路由。
5. 确认真实 ICMP 和通用 UDP 仍不可用，不能把用户态响应当作目标设备 `ping`；完成 Windows
   证据后再实现 Linux TUN 路由，取得两平台证据后才能把 SW-5 标记为 ✅。
