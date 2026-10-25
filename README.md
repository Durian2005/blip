# blip · 离信

> **Offline P2P messenger over Bluetooth LE — no network, no server, no account.**
> 断网也能用的点对点聊天工具：两台 Windows 电脑，没有 WLAN、没有热点、没有服务器，打开就能互发消息。

---

## 状态

**P0 · 项目骨架**（2026-10-25）

当前仓库只有可编译的空壳，**没有任何可用功能**。进度与任务拆解见 [开发计划-WBS.md](开发计划-WBS.md)，需求与验收标准见 [PRD-蓝牙离线通信软件.md](PRD-蓝牙离线通信软件.md)。

## 为什么做这个

微信、QQ、Telegram 都依赖互联网或至少局域网。一旦断网——教室 WiFi 挂了、宿舍路由器坏了、机房没有网络——它们全部失效，而此时两台电脑往往就在半米之内，只缺一条数据通路。

在"无 IP 网络"这个约束下，可选的点对点无线通道只有蓝牙：笔记本标配、免许可、不需要 IP 地址、系统级支持。Wi-Fi Direct 要求先开 WLAN，Wi-Fi 自建热点在 Windows 11 已被移除。

## 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET 9（`net9.0-windows`） |
| 界面 | WPF |
| 传输 | BLE GATT（`GattServiceProvider` / `BluetoothLEDevice`） |
| 协议 | 自定义二进制帧（非 JSON） |
| 加密 | X25519 + HKDF + ChaCha20-Poly1305，应用层端到端 |
| 存储 | SQLite（明文，个人自用） |

## 架构与引用方向

```
Lixin.App  (WPF)      ──▶ Lixin.Transport, Lixin.Security, Lixin.Core
Lixin.Transport       ──▶ Lixin.Core       通道实现（BLE / Loopback）
Lixin.Security        ──▶ Lixin.Core       密钥、握手、AEAD
Lixin.Core            ──▶ （零依赖）        领域模型、帧编解码、接口定义
Lixin.Tests           ──▶ 以上全部
```

**规则：依赖只能向下。** `Lixin.Core` 不引用任何项目，也不调用 Windows 专有 API——这样协议与安全逻辑可以脱离蓝牙硬件做单元测试。

核心设计是 `IByteChannel` 传输抽象：生产用 `BleGattChannel`，开发与自动化测试用 `TcpLoopbackChannel`，因此 80% 的逻辑在一台电脑上就能跑通，不必等第二台机器。

## 构建

```bash
dotnet build Lixin.sln
dotnet test  Lixin.sln
dotnet run   --project src/Lixin.App
```

要求：Windows 10 1809+ / Windows 11，.NET 9 SDK。

## 路线图

| 阶段 | 内容 |
|---|---|
| P0 | 项目骨架与仓库规范 ← **当前** |
| P1 | BLE 能力验证（能否稳定承担外设角色） |
| P2 | 传输骨架：帧层 + 分片 + Loopback 通道 |
| P3 | 真实 BLE 通道：发现、角色自适应、重连 |
| P4 | 安全层：E2EE + SAS 验证 |
| P5 | 产品化界面：会话、历史、待发队列 |
| P6 | 打磨与交付 |

## 设计约束（最小权限）

这个项目刻意保持克制：

- 不需要管理员权限，安装与运行全程用户态
- 不写启动项、不做托盘常驻、不留后台进程——关窗即退出
- 零出站请求：无遥测、无更新检查、无 CDN 依赖
- 数据只写 `%LOCALAPPDATA%\Lixin\`，不碰系统目录与共享目录
- 日志不记录消息明文与会话密钥

## License

[MIT](LICENSE)
