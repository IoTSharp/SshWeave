# 变更日志

## [未发布]

### 新增

- 对应路线图任务 `SW-5`、`SW-7` 和 `SW-8`，发布 `0.3.5` 现场候选：桌面 Native AOT 入口改为 `requireAdministrator`，程序启动即请求管理员权限；透明 TCP 路由设置保留 CIDR 兼容并追加单地址和连续尾部通配表达式，默认 `10.51.*.*` 规范化为 `10.51.0.0/16`，`10.*.*.*`、`10.165.*.*` 分别规范化为 `/8`、`/16`，非连续通配和默认路由继续拒绝。新增不含本地端口映射的焯烟 TCP 直通连接文件，离线确认 SOCKS、透明 TCP、严格主机校验和自动连接已启用且未保存认证口令；5,965 字节文件 SHA-256 为 `E2F7C81E8E8281F48D5C86953EC96A7843A4F96A6939D36B14EEC0C64917900A`。47 项测试、Release 零警告构建、两个 Windows x64 Native AOT 发布、管理员清单门禁、WiX 构建和 MSI 行政解包通过；8,011,776 字节 MSI SHA-256 为 `185C00BF78AC62F4A807D68209FEF2262D67C05FC16F5A1465DEA2624C4548D7`。MSI 和 EXE 尚未签名，管理员现场安装、VNC 直连和断开清理仍待验收，因此任务保持验证中。
- 对应路线图任务 `SW-7` 和 `SW-8`，新增 WiX 6 Windows x64 安装工程与 Native AOT 汇总脚本，生成 7,925,760 字节的真实 `SshWeave 0.3.3` MSI（SHA-256 `ACAB2CDE8576249E0E2A56F9432FFEB9E93E979E18E8010949C76E16A41EC6AF`），内嵌桌面端、CLI、固定透明 TCP 数据面、品牌图标和许可证，并由 Windows Installer 注册开始菜单、控制面板图标及带独立图标的 `.sshweave` 文件关联。新增当前用户 DPAPI 加密连接格式，可内嵌连接配置、私钥、严格主机指纹和可选认证口令；双击未保存口令的文件时先显示私钥口令输入框，再只在当前会话展开密钥并连接。修复口令窗口构造阶段的空引用及消息循环尚未就绪导致双击后只留下托盘进程的问题，启动检查已确认主窗口和私钥口令窗口同时可见。SSH、PowerShell、`netsh` 和透明通道辅助进程均隐藏在后台，密钥认证失败时显示针对性提示；原创 SshWeave 品牌资产同时用于可执行文件、主界面、托盘、MSI 和连接文件。32 项测试、Release 零警告构建、两个 `win-x64` Native AOT 发布及 MSI 数据库检查通过；目标机安装/升级/卸载、代码签名和真实连接由用户验收。
- 对应路线图任务 `SW-5`，新增 Windows 透明 TCP 路由候选：配置可声明目标 IPv4 CIDR，CLI 与桌面会在 OpenSSH SOCKS5 就绪后启动固定哈希的 `tun2socks 2.6.0` 和 Wintun `0.14.1`，创建 `SshWeave` 虚拟网卡、写入仅当前启动有效的目标路由，并在任一进程退出时反向清理。实现包含默认路由/非规范 CIDR/地址重叠/既有路由冲突拒绝、管理员权限与依赖哈希预检、单网卡会话锁、桌面开关和计量链路。新增 20,637,482 字节 `win-x64` 现场验证包，包含 CLI、桌面程序、固定数据面、许可证、脱敏配置、当前用户安装/卸载脚本、中文验证清单、证据采集脚本和 16 项 SHA-256 清单；31 项测试、Windows PowerShell 5.1 安装/卸载、独立解压及清单复核通过，ZIP SHA-256 为 `47F73D7859DBCB551B367975F1C7C2890C9E4069EB064F625941B2449B65FF1F`。两个 SshWeave 可执行文件仍未代码签名；真实管理员网卡、`10.51.12.35:22` Xshell 直连、异常回滚及 Linux TUN 留待下一步验证，当前不标记完成。
- 对应路线图任务 `SW-8`，新增 MewUI `0.19.1` Windows/Direct2D 桌面控制台：管理连接账户及系统默认、密码、私钥文件认证，使用当前用户的一次性命名管道实现 `SSH_ASKPASS` 且不持久化秘密；可在界面启停 SOCKS5、TCP 映射和压缩，查看 OpenSSH 交互/网络日志、实时连接数与双向字节，并通过原生 `Shell_NotifyIcon` 托盘菜单打开、连接、断开和退出。新增原子配置保存、隐藏回环转发与本地计量代理；3 项目 Release 零警告、23 项测试和 6,976,000 字节 `win-x64` Native AOT 发布通过（SHA-256 `D94D8F270D6F8C0816DB92555DAB389AD357F1438E617B5BB5E958837C8220AE`），真实跳板机两类认证及托盘菜单人工验收仍待完成。
- 新增 .NET 10 Native AOT `sshweave` CLI、`sshweave.config.v1` 源生成 JSON 配置和跨平台发布脚本。
- 新增基于系统 OpenSSH 的 SOCKS5 动态转发及显式 TCP 本地映射，默认只监听回环地址。
- 新增 `run` 代理环境、SOCKS5 标准输入/输出桥接及 OpenSSH `ProxyCommand` 配置生成，可在活动通道内直接 SSH 到内网设备。
- 新增 Linux 专用通道用户安装器，通过 `nologin`、受限公钥和 `MaxSessions 0` 禁止 Shell，仅允许客户端 TCP 转发。
- 新增 17 项配置、SSH 参数、服务端策略、SOCKS5 编码和源生成 JSON 测试；完成 Windows/Linux x64 Native AOT 发布，并在隔离 Linux `sshd` 中验证 TCP 通道成功且 Shell/exec 被拒绝。

### 修复

- 修复 Windows 路由冲突预检把“不存在目标路由”误判为 PowerShell 退出码 `1` 的问题；查询改为先枚举 IPv4 路由再筛选目标前缀，零匹配正常继续，真实 CIM 或权限错误仍会阻止启动。
- 对应路线图任务 `SW-7` 和 `SW-8`，发布 `0.3.4`：DPAPI 连接文件以受保护 DACL 原子创建会话目录，展开私钥只允许当前用户、SYSTEM 和 Administrators 访问，修复继承额外组读取权限后被 Windows OpenSSH 以 `bad permissions` 拒绝的问题；连接文件追加可选同名公钥，口令窗口新增“使用 ssh-agent”，无口令代理尝试强制 `BatchMode=yes`，使 `IdentitiesOnly=yes` 可匹配代理中的受口令身份且失败时快速返回。发布脚本新增 `dotnet` 非零退出门禁，避免失败后误报旧产物。34 项测试、Release 零警告构建、两个 Windows x64 Native AOT 发布、MSI 数据库检查及 `0.3.3 -> 0.3.4` 实机主升级通过；8,015,872 字节 MSI SHA-256 为 `ED047B0AA340DAF05DA397F22D888F010EA27F85937D09267C718884D74F75E1`。6,141 字节焯烟连接文件 SHA-256 为 `83D41EAA47154D502D5926B3A696C302DA048A61112BC88F34BB15DEF211D72B`，安装态已使用 Windows `ssh-agent` 建立 `2222 -> 10.51.11.132:22` 通道并读取目标 OpenSSH banner，未使用真实私钥口令。

### 安全

- 强制使用 `strict` 或 `accept-new` 主机密钥策略，禁止关闭主机密钥校验。
- 非回环本地监听要求显式设置 `allowRemoteClients=true`，服务端策略默认关闭 `PermitTunnel`。
- 服务器配置在重载前通过 `sshd -t` 和匹配用户的 `sshd -T -C` 复核，失败时回滚 drop-in。
