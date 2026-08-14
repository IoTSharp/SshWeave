# SshWeave 路线图

状态：✅ 已完成 · 🚧 进行中 · ⏳ 待处理 · ⚠️ 已阻塞 · 🧪 验证中

| 编号 | 状态 | 任务 | 验收证据 |
| --- | --- | --- | --- |
| SW-1 | ✅ | 建立 .NET 10 Native AOT CLI、V1 配置和系统 OpenSSH 边界 | Release 零警告构建、17 项测试、`ssh -G` 参数检查及 Windows/Linux x64 Native AOT 发布通过 |
| SW-2 | ✅ | 提供 SOCKS5、显式 TCP 映射和进程代理环境 | 参数构建、回环限制、源生成 JSON 和命令冒烟通过 |
| SW-3 | ✅ | 支持活动通道内直接 SSH 到内网设备 | SOCKS5 标准输入/输出桥接与 `ProxyCommand` 生成测试通过 |
| SW-4 | ✅ | 创建只能转发、不能 Shell 的服务端专用用户 | 隔离 Linux `sshd` 验证 `-N -L` 成功且 exec 被拒绝；`nologin`、受限公钥、`MaxSessions 0`、有效配置复核和失败回滚已实现 |
| SW-5 | ⏳ | 使用经过审查的本地 TUN 方案实现透明 TCP 路由 | Windows/Linux 上目标 CIDR 内 TCP 应用无需单独配置代理 |
| SW-6 | ⚠️ | 提供真实 ICMP 和通用 UDP 的可选 L3 模式 | 依赖 `PermitTunnel` 及远端返回路由或限定 NAT；未获得服务器变更验收前不得启用 |
| SW-7 | ⏳ | 增加安装包、代码签名、发行校验和端到端 SSH 测试 | Windows/Linux 发布资产和真实双主机测试通过 |
