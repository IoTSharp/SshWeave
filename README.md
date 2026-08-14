# SshWeave

SshWeave 通过系统 OpenSSH 客户端复用 SSH 服务器的网络可达性。客户端是 .NET 10 Native
AOT 命令行程序；服务端不常驻代理，只需要 OpenSSH。默认模式不修改服务端路由、NAT、
IP forwarding 或默认网关。

## 能力边界

| 流量 | 默认模式 | 使用方式 |
| --- | --- | --- |
| HTTP/HTTPS、数据库、自定义 TCP | 支持 | SOCKS5 或显式本地 TCP 映射 |
| SSH 到内网设备 | 支持 | 本地 TCP 映射，或生成 `ProxyCommand` 后直接 `ssh user@目标IP` |
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

## 安全说明

- 通道用户可以尝试连接 SSH 服务器可达的任意 TCP 目标，权限边界等价于该服务器的网络
  位置。生产环境应再用主机防火墙限制允许的目标网段和端口。
- 本地监听默认只允许回环地址。绑定 `0.0.0.0` 或其它非回环地址必须显式设置
  `allowRemoteClients: true`，并自行承担把内网入口暴露给局域网的风险。
- 所有进程参数通过结构化参数列表传给 `ssh`，不会交给本地 shell 拼接。
- SshWeave 不记录认证口令、私钥内容或通道载荷。
- 不建议在生产跳板机使用 TAP/桥接；二层广播、地址冲突和环路的影响面明显更大。

更详细的设计与 L3 取舍见 [架构说明](docs/architecture.md)，后续范围见
[路线图](ROADMAP.md)。

## 开发验证

```powershell
dotnet restore .\SshWeave.slnx
dotnet build .\SshWeave.slnx --configuration Release
dotnet test .\SshWeave.slnx --configuration Release --no-build
dotnet publish .\src\SshWeave\SshWeave.csproj --configuration Release --runtime win-x64
```
