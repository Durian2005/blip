# 代码审查报告 · 2026-10-26

> 审查范围：仓库当前 HEAD（`12e4823`，P2 完成态）全部 C# 源码、项目文件与测试，共 1680 行 / 5 个项目。
> 审查方式：通读 + **可运行探针验证**（临时测试文件，验证完已删除，仓库回到干净状态）。
> 审查时基线：`dotnet build` **0 警告 0 错误**，`dotnet test` **24 passed / 0 failed**。
> 结论：**分层与协议边界的设计是好的，但 P2 的分片重组有两个会在 P3 立刻爆发的缺陷，另有一个项目级配置问题会让 P3 的第一行代码编译不过。建议先修 C1–C3 再开 P3。**

---

## 一、结论速览

| 级别 | 数量 | 一句话 | 修复状态 |
|---|---|---|---|
| **C 严重（P3 前必修）** | 3 | 1 个编译不过的配置坑 + 2 个分片重组的静默数据损坏 | ✅ **已全部修复** |
| **H 高（P3/P4 前处理）** | 4 | 契约冲突、校验缺口、无界缓冲、序号回绕 | H1 ✅ 已修；H2/H3/H4 待 P4 |
| **M 中（可排期）** | 5 | 并发契约、相等性语义、魔法偏移、验证不验证、文档命名漂移 | M4 ✅、M5 ✅；M1/M2/M3 待办 |
| **L 低（随手）** | 6 | 工具参数解析、计数竞态、空项目、无 CI、无分析器 | 未动 |

三个 C 级问题都已用探针跑出实际输出，不是静态推断。修复过程与验证见本文 **§九**。

---

## 二、C 级问题（开始 P3 之前必须处理）

### C1 · `Lixin.Transport` 物理上无法使用 WinRT 蓝牙 API ★最要紧

**证据**：往 `Lixin.Transport` 放一个只引用 `Windows.Devices.Bluetooth` 的空类，编译直接失败：

```
error CS0246: 未能找到类型或命名空间名"Windows"
error CS0246: 未能找到类型或命名空间名"BluetoothLEDevice"
error CS0246: 未能找到类型或命名空间名"GattDeviceService"
```

**根因**：`Directory.Build.props` 给全仓库设了 `<TargetFramework>net9.0-windows</TargetFramework>`。
这个 TFM **不带 Windows SDK 版本号**，因此没有 WinRT 投影。`tools/Lixin.BleProbe` 之所以能用，是它自己覆盖成了 `net9.0-windows10.0.19041.0`。

**影响**：`Lixin.Transport.csproj` 的注释写着"蓝牙/WinRT 相关调用只能出现在本层"——但本层根本编译不了这类代码。**P3 第一行 `using Windows.Devices.Bluetooth;` 就会红。** 这个坑不提前发现，会卡在"明明照着 P1a 的代码抄却编译不过"上。

**修复选项**：

| 方案 | 做法 | 代价 |
|---|---|---|
| A（推荐） | `Lixin.Transport.csproj` 里覆盖 TFM 为 `net9.0-windows10.0.19041.0` | 一行配置；测试项目随即间接依赖 WinRT，但仍只在 Windows 跑 |
| B | 拆出 `Lixin.Transport.Ble`，Loopback 留在不带 SDK 版本的 Transport | 更干净，但多一个项目、多一层引用 |
| C | 全仓库统一抬到 `net9.0-windows10.0.19041.0` | 最简单，但 `Lixin.Core` 会丧失"纯逻辑"的编译期保护 |

选 A：Core 保持 `net9.0-windows` 即可继续起到"防止 Core 误用 Windows API"的隐性护栏作用。

---

### C2 · 单帧控制帧会劫持进行中的分片消息（P3 的保活必踩）

**证据**（探针实跑）：300 字节正文切成 3 片，在第 1、3 片之间插入一条 `Ping`（序号恰好接上）：

```
PROBE2 收到 Ping 时返回: Completed
PROBE2 被劫持的载荷: 104 B，结尾 = "AAPING"
PROBE2 残余正文片: Completed，当前缓冲 0 B
```

**发生了什么**：`MessageAssembler` **不记录当前消息的类型**。Ping 是单帧（`Frame.Create` 默认 `lastFragment: true`），所以它被当成"正文的续片 + 末片"：

1. `Add(ping)` 直接把 `"PING"` 拼进正文缓冲，因 `IsLastFragment` 为真而返回 `Completed`；
2. 交付 104 B（100 A + "PING"），`Reset()`；
3. 调用方按 `frame.Type` 分发 → 归到 `Ping` 分支，**这 104 B 正文被直接丢弃**；
4. 剩余正文片（seq 3）成为一条**新的**消息并再次 `Completed`（100 B）。

最终交付 204 B / 原 300 B，**静默丢 96 B，且两次都报成功**。

**为什么是 P3 必修**：P3 的保活 Ping、P5 的 ACK / 已读回执，全都是"在长消息传输过程中随时插入的单帧"。当前实现下，只要 32 KB 文本在发（64 帧、按实测速率持续数秒），中间来一条 Ping 就会损坏这条消息。

**修复方向**（按代价从低到高）：

1. **在 demux 层先按类型分流**：`Ping`/`Pong`/`Bye`/`Ack` 这类单帧类型不进 assembler，直接交给上层；只有 `Text`（以及未来的 `FileChunk`）走重组。改动最小，且不需要动线格式。
2. **assembler 记住 `_currentType`**，类型不符时返回 `ProtocolError` 并作废当前消息。能防住损坏，但会连带丢消息。
3. **帧头加 `MsgId`**（或 `StreamId`）：允许多条消息交错，彻底解决。代价是要改线格式，且必须现在改（P4 前，无兼容包袱）。

建议：**先做 1，同时在帧头预留 `MsgId` 字段**。P2 阶段 `MsgId` 恒为 0，P4 之后再启用——这样又回到"提前花 1 字节买未来"的老办法，且不必第二次改协议。

---

### C3 · 序号断裂后，残余分片被当作新消息并以"完整"交付

**证据**（探针实跑）：16 字节切 4 片，丢掉第 2 片：

```
PROBE1 断裂那一片: ProtocolError（这一步是对的）
PROBE1 断裂后: Completed, 交付 4 B = "mnop"（原消息 16 B）
```

**发生了什么**：`Reset()` 把 `_inMessage` 置回 false，于是**下一个到达的分片被当成新消息的开头**，一旦它的 `LastFragment` 为真就立刻 `Completed`。上游拿到的是一条"语法合法、内容被截断"的假消息，**没有任何错误信号**。

**危害高于丢消息**：丢消息用户会问"怎么没收到"；静默截断用户会以为内容完整。对聊天软件来说后者更难排查。

**修复**：`ProtocolError` 后进入**毒化状态**（`_poisoned = true`），丢弃后续所有帧直到遇到 `IsLastFragment` 才恢复接收；或干脆不重置 `_expectedSequence`，让后续片继续被判为断裂。前者语义更明确。

---

## 三、H 级问题

### H1 · `ReceiveAsync` 空缓冲返回 0，与"0 = 对端已关闭"契约冲突

接口注释明确写"返回 0 表示对端已关闭，不会再有任何数据"，但 `LoopbackChannel` 对空缓冲直接 `return 0`：

```
PROBE3 空缓冲返回 0，但 IsOpen=True
```

上层若把 0 当作 EOF 就会误判断线。修：空缓冲抛 `ArgumentException`（这是调用方的编码错误，不该走数据通路）。

### H2 · 不校验 `TAGLEN`，且 `AuthTagSize` 定义了却从未使用

`FrameCodec.TryParse` 与 `FrameReader` 都直接读 `source[11]` 作为标签长度，**没有任何合法性检查**：

- `TAGLEN` 可为 0–255，帧总长因此可达 779 B，而 `MaxPayload=512` 的约束只盖住了 `LEN`；
- `ProtocolConstants.AuthTagSize = 16` 全仓库**零引用**；
- P4 接入 AEAD 后，攻击者发一条 `TAGLEN=0` 的帧，就可能被解密层当成"未加密帧"放行——**这是安全校验缺口，不只是整洁问题**。

修：`FrameCodec` 校验 `tagLength` 只能是 `0` 或 `AuthTagSize`；补 `MaxFrameSize` 常量。

### H3 · `FrameReader` 缓冲无上限，且用 `List<byte>` 当环形缓冲

- **无上限**：对端持续推数据（帧头合法但永不满帧）可让 `_buffer` 无限增长 → 内存耗尽。P4 的安全章节里这属于必须堵的 DoS 面。
- **性能**：每解析一帧就 `RemoveRange(0, consumed)`，复杂度 O(剩余)。32 KB 消息按 7 B 分块到达时，会反复搬移同一批字节。

修：改用定长 `byte[]` + 读写指针（或 `ArrayBufferWriter<byte>` + 阈值压缩）；设 `MaxBufferedBytes`，超限即断开并计入诊断。

### H4 · 序号回绕后消息被永久丢弃

`frame.Sequence < _expectedSequence` 用于判重。`uint` 从 `MaxValue` 回到 0 时 `0 < 4294967295` 成立 → 被判为"重复帧"静默忽略。需 40 亿帧才触发，实际风险低，但 `_expectedSequence = uint.MaxValue` 同时充当哨兵值是明确的魔法值坏味道。

修：内部用 `long` 计数，或加 `bool _hasExpected` 显式表达"尚无期望值"。

---

## 四、M 级问题

| # | 问题 | 说明 |
|---|---|---|
| **M1** | `LoopbackChannel` 并发契约不完整 | `System.Random` 非线程安全却被并发 `SendAsync` 共享；`ReceiveAsync` 修改 `_current/_currentOffset` 共享字段，**并发读会重复交付**。接口只写了"Send 允许并发"，没写"Receive 必须单读者"。探针用 8 线程各发 4096 B 未复现损坏（全部 32768 B 到达），但这只说明"本次没测出来"。修：`Random.Shared` 或加锁；注释补明单读者约束 |
| **M2** | `Frame` 是 `record struct` 却含 `ReadOnlyMemory<byte>` | 自动生成的 `Equals` 按内存段引用比较、**不比较内容**。两个内容相同的帧不相等。当前无人使用，但将来在测试里写 `Assert.Equal(expected, actual)` 会莫名失败。修：比较器只作用于头部字段，或在文档里注明"不要用默认相等性" |
| **M3** | 帧头偏移硬编码散落三处 | `FrameCodec.Encode` 用 `[2]/[3]/[4]/AsSpan(5)/AsSpan(9)/[11]`，`TryParse` 再来一遍，`FrameReader` 又用 `span[9..]`。`ProtocolConstants` 只定义了总长 `HeaderSize`。改一次布局要同步动 3 个文件、约 8 处魔法数字。修：抽 `HeaderOffsets` 常量或 `FrameHeader` 结构 |
| **M4** | `ChatSpike` 的"验证"不做任何验证 | 结尾无条件打印 `=== 完成：所有消息完整重组，无丢帧 ===` 并 `return 0`；`if (assembler.Add(...) != Completed) continue;` 把 `ProtocolError` 静默吞掉。**收到 0 条消息也会打印成功。** 这意味着 P2 的结论应当被诚实表述为"在固定 seed 下跑通且无异常输出"，而不是"已证明无丢帧"。修：统计收发条数与字节数，不符则 `return 1` |
| **M5** | 文档写 `TcpLoopbackChannel`，代码里是 `LoopbackChannel` | 共 9 处：PRD 5 处、README 1 处、WBS 1 处、`Lixin.Transport.csproj` 注释 1 处、归档文档 2 处。README 是公开门面，指向一个不存在的类名。修：统一改 `LoopbackChannel`（它是内存实现，加 `Tcp` 前缀反而误导） |

---

## 五、L 级问题

| # | 问题 |
|---|---|
| **L1** | `BleProbe` 的 `--variant` 解析在 `try/catch` **之外**（`Program.cs` 37–42 行），传 `--variant abc` 会抛未捕获的 `FormatException` 直接崩溃 |
| **L2** | `_writeCount` / `_notifyCount` 用 `++` 而非 `Interlocked`，GATT 事件回调可能并发触发 |
| **L3** | `NotifyAsync` 先 `_notifyCount++` 再统计结果，0 个客户端成功也会打印 `[TX #n]`；"暂无订阅者，跳过通知"同样计入 |
| **L4** | `Lixin.Security` 是空项目（0 个源文件），但测试项目已引用它 |
| **L5** | 测试项目 TFM 继承 `net9.0-windows`，Linux CI 上跑不了；**当前完全没有 CI**（无 `.github/workflows`），24 个测试只靠手动跑。另：`GenerateDocumentationFile=false` 但公共 API 全都写了 XML 注释，既不产出文档也不产生缺失警告 |
| **L6** | 无 `.editorconfig`、无 analyzer 配置，`dotnet format` 没有基线，风格约束全靠自觉 |

---

## 六、做得对的地方（避免只列缺点）

1. **分层是真的落地了，不是画在图上**：`Lixin.Core` 零项目引用，且 TFM 不带 SDK 版本 —— 想在里面误用 Windows API 会直接编译失败。这条护栏是意外的，但很有效。
2. **`IByteChannel` 这条边界选得准**：`ValueTask` + `0 = EOF` 的 socket 语义、`SendAsync` 文档声明可并发，接口本身干净。P3 换通道确实不用动协议层。
3. **异常路径的测试覆盖是这份代码最扎实的部分**：逐字节到达、粘包、垃圾前缀、`LEN=0xFFFF`、重复帧、序号断裂 —— 六个恶心场景都有测试，不是只测了 happy path。
4. **`LoopbackChannel` 用内存 + 随机切块替代真 TCP 的判断正确**：没有监听端口（不触发防火墙），且 `seed` 可复现，`maxChunk: 1` 能压最坏情况。
5. **注释写的是"为什么"而非"是什么"**：例如为什么不用 StaticValue、为什么用 `--variant`、为什么 RX 也加 Read。这类注释比代码本身更值钱。

---

## 七、进度快照（截至 2026-10-26）

### 已完成

| 阶段 | 状态 | 产出 |
|---|---|---|
| **P0 项目基线** | ✅ 完成 | 五项目解决方案、`Directory.Build.props`、README / LICENSE(MIT) / BACKLOG / `.gitignore`；build 与 test 均通过 |
| **P1a 能力验证** | ✅ 完成（G1a 达成） | `tools/Lixin.BleProbe`；**真机实测**：外设角色支持、可连接广播 `Started`、iPhone 经 nRF Connect 双向收发成功（写 5 B / 通知 13 B，1/1 成功）；**R1 风险关闭**；未打包 exe 即可当外设 → 绿色版方案成立 |
| **P2 传输骨架** | ✅ 完成（代码完成，缺陷待修） | 帧编解码、增量解帧、分片/重组、`IByteChannel`、`LoopbackChannel`、24 个单测、`tools/Lixin.ChatSpike` |

### 尚未完成

| 阶段 | 状态 | 备注 |
|---|---|---|
| **P1b 双机端到端** | ⏳ 挂起 | 只有一台笔记本；需借一台 Windows 跑 2 小时。**最晚不能越过 G4** |
| **P3 真实通道** | ⛔ 未开始 | `BleGattChannel` + 角色选举 + 重连 + 环境自检。**被 C1 阻塞** |
| **P4 安全层** | ⛔ 未开始 | E2EE / SAS / 抓包验收。`Lixin.Security` 仍是空项目 |
| **P5 产品化 UI** | ⛔ 未开始 | WPF 界面、历史、待发队列 |
| **P6 打磨交付** | ⛔ 未开始 | 绿色版、文档 |

### 待办（按顺序）

1. **修 C1**（一行 TFM）→ 否则 P3 无法编译
2. **修 C2 + C3**（汇编器类型隔离 + 毒化状态）→ 否则 P3 保活会损坏消息
3. **补回归测试**：把本报告 C2 / C3 / H1 的探针转成正式测试（提交时应当**先红后绿**）
4. **开 P3**：`BleGattChannel` → 角色选举（§7.4.1 竞选键）→ 重连退避 → 环境自检
5. 穿插：`ChatSpike` 加断言（M4）、统一 `LoopbackChannel` 命名（M5）、补 CI（L5）

---

## 八、新对话接手指引

| 想了解 | 看哪里 |
|---|---|
| 产品做什么、不做什么 | `PRD-蓝牙离线通信软件.md`（v1.2）§2、FR-01~FR-11 |
| 施工顺序、门禁、估算 | `开发计划-WBS.md`（v1.1）§1、§5、§6 |
| 协议线格式 | `PRD` §7.5 + `src/Lixin.Core/Protocol/ProtocolConstants.cs` |
| 实测数据（真实数字） | `PRD` §13 附录 C |
| 新想法往哪放 | `BACKLOG.md`（范围冻结到 G4，不打断施工） |
| 本报告 | `docs/code-review-2026-10-26.md` |

**接手时先确认三件事**：① `git log --oneline` 是否仍为 5 个提交、工作区是否干净；② `dotnet test` 是否仍 24 passed；③ C1–C3 是否已修（`grep -n "net9.0-windows10" src/Lixin.Transport/Lixin.Transport.csproj`，以及看 `MessageAssembler` 是否已有 `_currentType` / `_poisoned`）。

**约定仍然有效**：任何 `git push` 都要先列清单等用户确认；脱敏三查（工作区 → 全历史 → 个人设备清单），扫描命令逐模式跑，不让"命令报错"和"没有匹配"共用一个分支。

---

## 九、修复记录（2026-10-26，同日完成）

按「先红后绿」执行：先补测试断言**正确行为**并确认失败，再改实现至全绿。

### 测试先红（8 条新测试，7 条失败）

```
失败 LostMiddleFragment_ResidualTailIsNotDeliveredAsCompleteMessage   Expected: Discarded  Actual: Completed
失败 SingleFrameControlMessage_DoesNotHijackInFlightText              Expected: Completed  Actual: NeedMore
失败 ControlFrames_DoNotConsumeDataSequenceSpace                      Expected: Completed  Actual: NeedMore
失败 FragmentedControlFrame_IsRejectedWithoutPoisoningText
失败 AfterProtocolError_NextCompleteMessageIsAccepted                 Expected: Discarded  Actual: Completed
失败 EmptyReadBuffer_ThrowsInsteadOfSignallingEof                     No exception was thrown
失败 Reset_ClearsPoisonedState                                        Expected: True       Actual: False
```

### 改了什么

| # | 缺陷 | 改动 | 位置 |
|---|---|---|---|
| 修1 | C1 | 全仓库 TFM → `net9.0-windows10.0.19041.0`；`Lixin.Core` 反向降到纯 `net9.0` | `Directory.Build.props`、`Lixin.Core.csproj`、`Lixin.BleProbe.csproj` |
| 修2 | C2 | 新增 `FrameTypes.IsFragmented`；控制帧旁路重组器并改用独立序号空间 | `FrameType.cs`、`MessageAssembler.cs` |
| 修3 | C3 | 引入毒化状态 + `AssemblyResult.Discarded` + `IsPoisoned` | `MessageAssembler.cs` |
| 修4 | H1 | 空读缓冲抛 `ArgumentException`；接口补明「单读者」 | `LoopbackChannel.cs`、`IByteChannel.cs` |
| 修5 | M4 | `ChatSpike` 改为真断言 + 退出码；正文中间主动插 Ping | `tools/Lixin.ChatSpike/Program.cs` |
| 修6 | M5 | `TcpLoopbackChannel` → `LoopbackChannel`（4 个文件） | PRD / README / WBS / csproj |

### 修复时发现的两处设计分歧（已按更简单的方案定稿）

1. **「丢弃 N 帧后强制解除毒化」这个补丁不能要。** 一旦在中途解除，后续帧会从消息中间开始组装，合法的新消息反而被判成断裂。改为：毒化只到「下一个末片」或 `Reset()` 为止。因为每条完整消息都以末片结束，所以**失聪天然有界——最多再白丢一条消息**，不需要额外计数。
2. **控制帧必须用独立序号空间。** 否则即使旁路了重组器，控制帧仍会吃掉一个数据序号，在正文中间插出空洞，把完好正文判成断裂。这一条已写进 PRD §7.5.1。

### 修复后验证

| 检查 | 结果 |
|---|---|
| `dotnet build` | 0 警告 0 错误 |
| `dotnet test` | **32 passed / 0 failed**（24 原有 + 8 新增） |
| C1 复验 | 在 `Lixin.Transport` 放 `using Windows.Devices.Bluetooth;` + `BluetoothLEDevice`/`GattDeviceService` → **编译通过** |
| 端到端 | `ChatSpike` 在 21600 B 正文第 10 片后插入 Ping → 正文一字不差、Ping 单独交付、退出码 0 |

```
[B] 收到保活 PING（未干扰正文）
[B] 收到 Text 21600 B：「长文本压力测试；…」
结果：PASS —— 全部消息完整、顺序一致、无重复、无丢弃
```

### 仍然未修的（明确保留）

| 项 | 为什么留 |
|---|---|
| H2 `TAGLEN` 校验 | 属于安全校验，**必须在 P4 接 AEAD 之前一起做**，届时才知道要接受的取值范围 |
| H3 缓冲无上限 | DoS 面，P4 安全章节处理；当前无网络暴露，风险低 |
| H4 序号回绕 | 40 亿帧才触发，实际不可达 |
| M1/M2/M3 | 不影响正确性；M3 建议在 P3 首次改 `FrameCodec` 时顺手做 |
| L4/L5/L6 | CI 与分析器属于工程化，可在 P6 前补 |
