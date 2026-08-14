# SshWeave 架构说明

## 默认数据路径

```text
应用或 OpenSSH
      |
      | SOCKS5 / 本地 TCP / ProxyCommand
      v
SshWeave 客户端 -> 系统 OpenSSH -> SSH direct-tcpip -> SSH 服务器发起目标 TCP 连接
                                                            |
                                                            v
                                                     内网 TCP 设备
```

SshWeave 是控制面和本地数据桥接程序。SSH 加密、认证、主机密钥验证和 `direct-tcpip`
通道由系统 OpenSSH 实现，避免自行实现密码协议。远端连接的源地址是 SSH 服务器自身，目标
设备按普通服务器访问处理，因此不需要修改 SSH 服务器的路由、NAT 或目标网络的返回路由。

## 为什么默认模式不能承载 `ping` 和 UDP

OpenSSH 的动态转发实现 SOCKS4/5 `CONNECT`，本地转发使用 `direct-tcpip`。两者都描述一个
TCP 目标主机和目标端口。ICMP 没有端口，OpenSSH 动态转发也没有实现 SOCKS5
`UDP ASSOCIATE`，所以本地路由或 TUN 只能把这些数据包交给客户端，却没有可用于第二段
传输的标准 SSH 通道。

把 ICMP 本地伪装成成功响应会造成错误诊断；使用 TCP 端口探测可以判断具体服务是否可达，
但不能称为真实 `ping`。

## 可选 L3 模式的影响

OpenSSH `-w`/`PermitTunnel point-to-point` 能传输三层 IP 包，但只负责创建 TUN 通道。完整
连通还需要：

1. 为客户端和服务端 TUN 分配不冲突的专用地址；
2. 客户端只向 TUN 添加明确的远端 CIDR；
3. 服务端允许 TUN 与目标网卡之间转发；
4. 目标网络添加返回隧道网段的路由，或服务端对该隧道源网段执行限定 NAT；
5. 防火墙只放行指定源、目标、协议和连接状态，并提供可靠回滚。

`PermitTunnel` 本身可以只对专用用户生效，不会改默认路由。实际的 IP forwarding、TUN 地址、
防火墙和 NAT 会改变服务器网络状态。最小影响方案不改默认路由，NAT 规则同时限定隧道源
网段、目标 CIDR 和出口网卡；如果远端网关能添加精确返回路由，则可以不使用 NAT，但网络
改动只是从 SSH 服务器转移到了网关。

当前版本不自动应用这些服务器网络改动。后续 L3 实现必须先提供只读预检、规则预览、冲突
检测、原子应用、断线清理和独立回滚命令，取得真实 Linux 服务器证据后才能标记完成。

## 通道用户边界

服务端安装器采用两层限制：

- Unix 用户 shell 为 `nologin`，公钥带 `restrict,port-forwarding`；
- `Match User` 使用 `MaxSessions 0`，仅开放客户端 TCP forwarding，并关闭密码认证、PTY、
  Agent、X11、Unix socket forwarding、用户 rc 和 `PermitTunnel`。

`MaxSessions 0` 禁止 shell、exec 和 subsystem 会话，但按 OpenSSH 语义仍允许转发。这比
`ForceCommand internal-sftp` 更符合“只开通道”的目标，后者会额外开放不需要的 SFTP。

## 兼容性策略

配置格式固定为 `sshweave.config.v1`。已发布字段只追加扩展；需要破坏性改变时使用新的模式
编号。JSON 使用 .NET 源生成序列化元数据，项目启用 AOT 和裁剪分析，不在运行时扫描类型。
