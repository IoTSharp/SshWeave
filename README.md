# SshWeave

SshWeave 通过系统 OpenSSH 客户端复用 SSH 服务器的网络可达性。客户端包含 .NET 10 Native
AOT 命令行程序和基于 MewUI 的 Windows 桌面控制台；服务端不常驻代理，只需要 OpenSSH。
默认模式不修改服务端路由、NAT、IP forwarding 或默认网关。

## 能力边界

| 流量 | 默认模式 | 使用方式 |
| --- | --- | --- |
| HTTP/HTTPS、数据库、自定义 TCP | 支持 | SOCKS5 或显式本地 TCP 映射 |
| SSH 到内网设备 | 支持 | 本地 TCP 映射，或生成 `ProxyCommand` 后直接 `ssh user@目标IP` |
| Windows 目标网段透明 TCP | 验证中 | Wintun + 本地 `tun2socks`，应用直接连接目标 IP 和端口 |
| 服务端解析的内网域名 | 支持 | 使用 `socks5h://`，域名交给 SSH 服务器解析 |
| 通用 UDP | 不支持 | 标准 OpenSSH 动态/本地转发没有 UDP 通道 |
| 真实 ICMP `ping` | 不支持 | ICMP 没有端口，不能转换为 `direct-tcpip` |

要支持真实 ICMP 和任意 UDP，必须改用 OpenSSH `PermitTunnel` 三层 TUN，并为返回流量配置
远端路由或范围严格限定的 NAT。只在本机添加路由不能解决 SSH 服务器到目标设备的回程问题。
这会改变服务器网络状态，因此不属于当前默认交付。

## 快速开始

### 1. 发布客户端

```powershell
.\scripts\publish.ps1 -RuntimeIdentifier win-x64
```

Linux x64 需要在 Linux 构建机运行：

```powershell
./scripts/publish.ps1 -RuntimeIdentifier linux-x64
```

生成物位于 `artifacts/publish/<RID>`。SshWeave 自身没有 NuGet 运行时依赖，但客户端必须能
执行 OpenSSH `ssh`。

Windows 桌面控制台使用 MewUI/Direct2D，并单独发布：

```powershell
.\scripts\publish.ps1 -RuntimeIdentifier win-x64 -Desktop
```

正式 Windows Installer 包使用 WiX 6 构建，安装桌面端、CLI、透明 TCP 数据面和许可证，
同时注册开始菜单、控制面板品牌图标与带独立图标的 `.sshweave` 文件关联：

```powershell
.\scripts\build-windows-msi.ps1
```

生成物位于 `artifacts/packages/SshWeave-<版本>-win-x64.msi`。安装和卸载由 Windows
Installer 管理，不再使用文件复制脚本。`0.3.5` 起桌面程序入口清单使用
`requireAdministrator`，从开始菜单或双击 `.sshweave` 启动时会立即显示 Windows UAC，
确保后续创建 Wintun 网卡和活动路由时已经持有管理员令牌。

桌面控制台管理多个连接账户，支持系统默认认证、密码和私钥文件。密码及密钥口令只在本次
连接期间驻留内存，通过当前用户可访问的一次性命名管道交给 `SSH_ASKPASS`，不会写入配置、
命令行或日志。界面可启停 SOCKS5、TCP 映射和压缩，显示 OpenSSH 交互日志、实时连接数与
本地计量代理统计的上下行字节；关闭主窗口后继续驻留 Windows 托盘，右键菜单可重新打开、
连接、断开和退出。应用窗口、侧栏、托盘、MSI 和连接文件使用统一品牌资产；SSH、PowerShell、
`netsh` 和透明通道辅助进程均隐藏在后台。远端系统用户写操作未在首版启用。

### 2. 创建只能使用通道的服务端用户

先用管理员账户登录 Linux SSH 服务器，把客户端公钥和对应平台的 `sshweave` 二进制放到
服务器临时目录，然后以 root 运行：

```bash
./sshweave server-install \
  --user sshweave \
  --authorized-key /tmp/sshweave-client.pub
```

安装命令会：

- 创建 shell 为 `nologin` 的专用用户；
- 把公钥写成 `restrict,port-forwarding` 授权；
- 写入只允许客户端 TCP 转发的 `sshd_config.d` 策略；
- 使用 `sshd -t` 和 `sshd -T -C` 验证最终有效配置；
- 验证成功后重载 SSH 服务，失败则回滚配置片段。

策略使用 `MaxSessions 0` 禁止 Shell、命令和子系统会话，同时保留 `-L`/`-D` 所需的
`direct-tcpip`。它明确关闭 PTY、密码登录、Agent/X11/Unix socket 转发和 `PermitTunnel`。
仅查看配置而不安装：

```bash
./sshweave server-config --user sshweave
```

### 3. 创建客户端配置

```powershell
sshweave init
```

默认路径为 Windows `%APPDATA%\SshWeave\config.json`，Linux
`$XDG_CONFIG_HOME/sshweave/config.json` 或 `~/.config/sshweave/config.json`。示例：

```json
{
  "schema": "sshweave.config.v1",
  "sshExecutable": "ssh",
  "defaultProfile": "station",
  "profiles": [
    {
      "name": "station",
      "host": "bastion.example.com",
      "port": 22,
      "user": "sshweave",
      "authenticationMode": "keyFile",
      "identityFile": "C:\\Users\\operator\\.ssh\\sshweave_ed25519",
      "hostKeyPolicy": "strict",
      "batchMode": true,
      "socks": {
        "listenAddress": "127.0.0.1",
        "port": 1080
      },
      "tcpForwards": [
        {
          "listenAddress": "127.0.0.1",
          "localPort": 2222,
          "destinationHost": "10.20.0.10",
          "destinationPort": 22
        }
      ]
    }
  ]
}
```

生产环境应预先登记服务端主机密钥并使用 `hostKeyPolicy: "strict"`。`accept-new` 会固定首次
看到的密钥，但第一次连接仍可能受到中间人攻击。配置中只保存私钥路径，不保存口令或私钥。

需要把连接配置、私钥、固定的 `known_hosts` 条目和可选认证口令交付为一个文件时，可在
Windows 当前用户上下文创建 DPAPI 加密的 `.sshweave` 文件：

```powershell
sshweave connection-create `
  --config .\config.json `
  --profile station `
  --known-hosts .\station.known_hosts `
  --output .\station.sshweave
```

需要同时保存密码或私钥口令时添加 `--secret-stdin`，口令从标准输入读取，不进入命令行。
该文件只能由创建它的 Windows 用户解密。安装 MSI 后双击文件会启动桌面端、载入内嵌私钥
和主机指纹；创建时若私钥旁存在同名 `.pub` 文件，也会一并内嵌，以便 OpenSSH 在
`IdentitiesOnly=yes` 下匹配 Windows `ssh-agent` 中的身份。未保存认证口令时可输入本次口令，
也可明确选择“使用 ssh-agent”。私钥只在当前应用会话中展开到受保护的用户本地临时目录，
目录 ACL 只允许当前用户、SYSTEM 和 Administrators，退出时删除。

### 4. 登录并使用

先检查本地端口、文件和 OpenSSH 参数：

```powershell
sshweave check --profile station
sshweave connect --profile station
```

保持该窗口运行。另一个窗口可以访问 SOCKS5：

```powershell
curl --proxy socks5h://127.0.0.1:1080 https://device.internal/
```

也可以让 SshWeave 启动一个遵循代理环境变量的程序，并在程序退出时自动关闭通道：

```powershell
sshweave run --profile station -- curl https://device.internal/
```

显式映射示例中的目标设备 SSH：

```powershell
ssh -p 2222 device-user@127.0.0.1
```

### 5. 直接执行 `ssh user@内网IP`

在 `sshweave connect` 运行期间，生成 OpenSSH 配置片段：

```powershell
sshweave ssh-config --profile station --match "10.* 192.168.*"
```

将输出纳入用户 OpenSSH 配置后，可直接执行：

```powershell
ssh device-user@10.20.0.10
```

生成的 `ProxyCommand` 调用 `sshweave socks-connect %h %p`，通过已经登录的 SOCKS5 通道
桥接 SSH 标准输入/输出。若 `sshweave` 不在 `PATH`，生成时使用
`--executable <绝对路径>`。

### 6. Windows 透明 TCP 路由候选

Windows 发布物包含经过固定版本和 SHA-256 校验的 `tun2socks 2.6.0` 与 Wintun `0.14.1`。
在连接配置中启用目标网段：

```json
"transparentTcp": {
  "enabled": true,
  "adapterName": "SshWeave",
  "adapterIpv4Cidr": "198.18.0.1/30",
  "mtu": 1500,
  "routeMetric": 5,
  "destinationCidrs": ["10.51.*.*"]
}
```

`destinationCidrs` 保持兼容原有规范 CIDR，并追加接受单个 IPv4 地址和连续尾部通配表达式。
例如 `10.*.*.*`、`10.165.*.*` 和 `10.51.11.*` 会分别规范化为 `10.0.0.0/8`、
`10.165.0.0/16` 和 `10.51.11.0/24`；当前站点默认 `10.51.*.*`，即只路由
`10.51.0.0/16`。通配符不得出现在固定地址段之间，`*.*.*.*` 等价默认路由并会被拒绝。

保持 SOCKS5 同时启用，并以管理员身份运行 CLI 或桌面控制台。连接就绪后，TCP 应用可直接
访问目标地址，例如 VNC 客户端直接连接 `10.51.11.132` 的现场 VNC 端口，无需配置代理或
本地端口映射。SshWeave 只写入
`store=active` 的目标路由；正常断开或任一数据面进程退出时会撤销路由和 TUN 地址。
该候选不承载真实 ICMP 或通用 UDP，管理员实机、异常回滚和 Linux TUN 证据仍按 SW-5
下一步验证执行。

## 安全说明

- 通道用户可以尝试连接 SSH 服务器可达的任意 TCP 目标，权限边界等价于该服务器的网络
  位置。生产环境应再用主机防火墙限制允许的目标网段和端口。
- 本地监听默认只允许回环地址。绑定 `0.0.0.0` 或其它非回环地址必须显式设置
  `allowRemoteClients: true`，并自行承担把内网入口暴露给局域网的风险。
- 所有进程参数通过结构化参数列表传给 `ssh`，不会交给本地 shell 拼接。
- SshWeave 不记录认证口令、私钥内容或通道载荷。
- Windows 桌面版通过隐藏回环端口承接 OpenSSH 转发，并在公开本地入口前计量连接与字节；
  不解析 SOCKS5 或 TCP 载荷内容。
- 不建议在生产跳板机使用 TAP/桥接；二层广播、地址冲突和环路的影响面明显更大。

更详细的设计与 L3 取舍见 [架构说明](docs/architecture.md)，后续范围见
[路线图](ROADMAP.md)。

## 开发验证

```powershell
dotnet restore .\SshWeave.slnx
dotnet build .\SshWeave.slnx --configuration Release
dotnet test .\SshWeave.slnx --configuration Release --no-build
dotnet publish .\src\SshWeave\SshWeave.csproj --configuration Release --runtime win-x64
dotnet publish .\src\SshWeave.Desktop.Windows\SshWeave.Desktop.Windows.csproj --configuration Release --runtime win-x64
```
