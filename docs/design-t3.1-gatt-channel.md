# T3.1 `BleGattChannel` 编码前设计

> 定位：PRD §7.4 已经定了通道的**形态**，本文只定 PRD 还没落到实现粒度的四个决策 + 一张 UUID 表 + 一张错误映射表。
> 本文写完即进入编码，期间不再翻案；真机验证后若有修正，改本文并记录原因。

## 0. 一句话结论

`IByteChannel` 是**字节流**，不是消息流。通道层只负责「把任意长度的字节可靠、有序地塞进 GATT 的 PDU，再把收到的字节原样吐出来」。
所以通道层**不加任何帧头、不做消息边界、不需要应用层 ACK** —— 这些都已经是帧层（`FrameCodec` / `FrameReader`）的职责。两层都做一遍等于双倍复杂度、双倍出错面。

## 1. 对齐：PRD 已定的，不再讨论

| 项 | 结论 | 出处 |
|---|---|---|
| 服务结构 | 1 个自定义 Service + TX（可写）+ RX（可通知）+ META（可读元信息） | §7.4 |
| 中心 → 外设 | `WriteWithoutResponse` 写 TX 特征值 | §7.4 |
| 外设 → 中心 | `Notify` 推 RX 特征值（中心订阅） | §7.4 |
| 分片 | 按协商 MTU 自适应，下限 20 B，上限 512 B | §7.4 |
| 角色选举 | 已有 `Lixin.Core/Election/`（T3.2 ✅），通道层只消费其结果 | §7.4.1 |
| 保活 | 应用层 PING，外设侧 20 s 无流量则发 | §7.5 |

## 2. 决策 A：MTU 采用「观测」，不做「请求」

**事实**：WinRT 没有「请求指定 MTU」的 API。中心侧只能读 `GattSession.MaxPduSize` 并订阅 `MaxPduSizeChanged`；协商由系统协议栈自己完成，应用层只能被动接受结果。

**推论**：编码时不能假设 MTU 是某个值。必须：

1. 连接建立后读一次 `MaxPduSize`；
2. 订阅 `MaxPduSizeChanged`，运行中变了就跟着变；
3. 单次写入的**有效载荷 = MaxPduSize − 3**（ATT Write Request: opcode 1 B + handle 2 B；Handle Value Notification 同样 −3）；
4. 未取到值时按 23 兜底 → 有效载荷 20 B（最保守，永远正确）。

**两层分片的分工必须写进注释，否则后人一定会混淆**：

| 层 | 块大小 | 目的 | 是否带帧头 |
|---|---|---|---|
| 帧层 `MessageFragmenter` | 512 B payload | 把 >512 B 的消息切成可重组的帧 | 有（12 B 帧头 + TAGLEN） |
| 通道层 `BleGattChannel` | MTU − 3（20 ~ 244 B） | 把字节塞进 ATT PDU | **无** |

通道层的切块对上层**完全透明**：接收端只是把收到的字节块依次拼进缓冲，`ReceiveAsync` 按调用方给的 buffer 大小吐出。因为字节流有序，拼起来天然正确，不需要任何额外协议。

## 3. 决策 B：写模式用 `WriteWithoutResponse`，但要盯住返回值

PRD 选 `WriteWithoutResponse` 是对的，理由不是"省一个往返"，而是**发射端的流控本来就该在这里做**。但要把「谁会丢字节」这件事说准，否则会做错防御：

**不会丢的地方**：BLE 链路层自带 CRC 校验 + 自动重传（LL retransmission）。一个 Write Command 不会在射频层被"咬掉半截"。所以**不需要**在通道层再做字节级 ACK/重传——那是把链路层已经做过的事再做一遍。

**真正会丢的地方**：本地发送队列溢出。这一层在 WinRT 里的实际表现是**背压**而不是错误码——
`NotifyValueAsync` 在协议栈收不下时会迟迟不返回，`await` 自然限流。

> ⚠️ **编码期修正（2026-11-01）**：本文初稿写的是「队列满返回 `InsufficientResources`」，
> 这是错的。`GattCommunicationStatus` 只有 `Success` / `Unreachable` / `ProtocolError` / `AccessDenied`
> 四个值，没有 `InsufficientResources`（那是 `BluetoothError` 家族的命名风格）。
> 实现时改为「对 `Unreachable` 退避重试」，见 §10。

**所以处置是**：

- 每次写都要检查返回的 `GattCommunicationStatus`（`NotifyValueAsync` 返回的是**每个订阅者一条**结果，取最差）；
- `Success` → 继续下一块；
- `Unreachable` → 退避 10 ms 后重试，最多 20 次（约 200 ms）；
- 其它状态 / 重试耗尽 → **断链 + 记日志**，绝不跳过这一块继续写。

跳过一块的后果是字节流中间少一段，帧层会解析出乱码帧，而现象是"偶尔消息内容损坏"——这是最难定位的一类故障。**宁可断线，不要错位。**

> 顺带澄清一个常见误解：帧层的 `ACK`（TYPE 0x11）是**消息级**送达回执（给人看的"已送达"），不是字节级可靠性机制。它不能、也不应该用来兜通道层的丢字节。

## 4. 决策 C：GATT 回调里绝不允许阻塞

这是 T3.1 最容易写错的一点。

`GattWriteRequested`（外设侧收数据）和 `ValueChanged`（中心侧收通知）都跑在**系统线程**上。如果在这两个回调里 `await` 一个可能长时间阻塞的操作（比如等上层来取数据），后果是整个 GATT server / client 停摆——表现为"连接还在但完全不收数据了"，而且不会报错。

**所以接收路径固定为三步，全部同步完成**：

```
回调（系统线程）
  1. 拷出字节（value 的 buffer 只在回调内有效，必须复制）
  2. inbox.Writer.TryWrite(块)   ← 同步尝试，不 await
  3. Respond()（外设侧；中心侧无需应答）
```

**入队失败怎么办？** `TryWrite` 失败 = 缓冲溢出 = 即将丢字节。这里只有两个选择：丢字节（帧层错位，难查）或断链（现象明确）。**选断链**，并记一条明确日志。

队列容量定 **256 块**。按最大 244 B/块算约 62 KB，足以吸收单条 32 KB 消息（64 帧 × ~512 B）的突发，而内存占用可忽略。

队列是**单读者**的（`IByteChannel` 契约已声明），所以 `ReceiveAsync` 从 inbox 取块、按调用方 buffer 大小切片吐出，逻辑与 `LoopbackChannel` 完全一致。

## 5. 决策 D：UUID 一次定死，之后永不修改

Windows 侧改了会生效，iOS 侧不会——iOS 按设备缓存对端 GATT 表，改 UUID 后手机仍看到旧的，表现为「新特征值凭空消失」，只能靠 iPhone 忽略设备或重启恢复。这是 T3.1 调试期最容易浪费半天的地方。

**解法：UUID 现在就写进 `Lixin.Transport/BleGattUuids.cs` 常量，编码期间不再改。**若真需要改，必须同步做「iPhone 忽略设备 + 重启」再验证。

| 常量 | UUID | 属性 |
|---|---|---|
| `Service` | `6c697869-6e00-4f1a-9c3d-1a2b3c4d5e60` | 主服务 |
| `TxCharacteristic` | `6c697869-6e01-4f1a-9c3d-1a2b3c4d5e61` | Write / WriteWithoutResponse（中心 → 外设） |
| `RxCharacteristic` | `6c697869-6e02-4f1a-9c3d-1a2b3c4d5e62` | Notify（外设 → 中心） |
| `MetaCharacteristic` | `6c697869-6e03-4f1a-9c3d-1a2b3c4d5e63` | Read（协议版本 + 昵称 + 能力位） |

前缀 `6c697869-6e00` 是 `"lixin"` 的 ASCII（`6c 69 78 69 6e`），便于抓包时人眼识别；后缀避开 Bluetooth SIG 保留的 `00805f9b34fb`。

**广播侧**：Service UUID 同时放进广播数据（`GattServiceProviderAdvertisingParameters.ServiceData`），对端按它过滤 —— 这是 FR-03 的实现口径。发现载荷的 18 B 格式见 PRD §7.4.2，已由 `DiscoveryPayload` 实现。

## 6. 错误映射表（给 T3.4 用，T3.1 先埋好分类）

| 情形 | 判定方式 | 处置 | 写入能力档案 |
|---|---|---|---|
| 适配器不存在 / 蓝牙关闭 | `BluetoothAdapter.GetDefaultAsync()` 为 null，或 `Radio.State != On` | 提示「蓝牙未开启」+ 唤起 `ms-settings:bluetooth` | 否 |
| 外设角色不支持 | `IsPeripheralRoleSupported == false`（静态可查） | 直接当中心，不尝试广播 | 是（`peripheralUnsupported = true`） |
| 服务注册失败 | `GattServiceProvider.CreateAsync` 返回 `Error` | 提示「本机无法作为被连接方」，转纯中心模式 | 是 |
| 广播启动失败 | `StartAdvertising` 抛异常 | 同上 | 是 |
| 连接被拒 / 超时 | `FromBluetoothAddressAsync` 拿到 null，或 `GattSession` 未进入 Connected | 退避重连（T3.3） | 否 |
| 发送队列溢出 | `InsufficientResources` 重试耗尽 | 断链，交 T3.3 重连 | 否 |
| 接收缓冲溢出 | `TryWrite` 返回 false | 断链，交 T3.3 重连 | 否 |

注意「写入能力档案」这一列：**只有"本机做不到外设角色"才写**，链路质量导致的失败一律不写——否则一次信号不好就会让本机永久放弃外设资格（T3.2 已经踩过这个坑，`CapabilityProfile` 的注释里记着）。

## 7. 代码落点

```
src/Lixin.Transport/
  BleGattUuids.cs              ← 常量（决策 D）                 ✅
  BleGattFraming.cs            ← MTU 观测 + 切块（决策 A）        ✅
  BleGattInbox.cs              ← 同步入队 + 溢出即断链（决策 C）   ✅
  BleGattPeripheralChannel.cs  ← 外设侧（决策 B + 接收路径）      ✅ 待真机验证
  BleGattCentralChannel.cs     ← 中心侧：扫描 + 连接 + 订阅通知   ⬜ 未开始
```

`IByteChannel` 签名一个字都不改。`LoopbackChannel` 与两个新实现的关系是并列的，上层（`FrameReader` / `MessageAssembler`）完全不知道底下是内存还是蓝牙——这是 P2 阶段就固化好的边界，T3.1 不许突破。

`SendAsync` 契约是「允许并发调用，顺序由实现保证」，所以两个实现都要用 `SemaphoreSlim(1,1)` 串行化发送。

## 8. 验收清单（G2 的 T3.1 部分）

当前只有一台 Windows，对端用 iPhone + nRF Connect，因此**先验能力、后验产品**：

| # | 验证项 | 判据 |
|---|---|---|
| 1 | 外设侧服务能被发现 | 手机 nRF Connect 按 Service UUID 扫到，且能看到 4 个特征值 |
| 2 | 外设侧收数据 | 手机写 TX 特征值 → 本机 `ReceiveAsync` 吐出相同字节 |
| 3 | 外设侧发数据 | 本机 `SendAsync` → 手机收到通知，字节一致 |
| 4 | 大块透明分片 | 本机一次 `SendAsync` 发 32 KB → 手机侧按序收全，字节一致 |
| 5 | 中心侧 | 需第二个可当外设的对端；手机 nRF Connect 可当外设，用它验 |
| 6 | 断链语义 | 手机断开连接 → 本机 `ReceiveAsync` 返回 0（不是抛异常、不是永久挂起） |
| 7 | 日志 | 上述每条都有对应日志行，不含明文消息内容 |

第 5 项依赖手机侧能当外设（已实测 iPhone XS Max 支持，见 P1 记录）。

## 9. 明确留白：这些只能真机确认

以下事项**只能真机确认**，编码时不凭猜测硬编码 —— 已按此原则做成可替换点 + 日志探针，
答案见 §10。

1. **外设侧如何感知中心断开**——`GattServiceProvider` 没有直接的 `Session` 属性，候选是 `SubscribedClientsChanged` 或 `GattSession`。
2. **外设侧收到 `WriteWithoutResponse` 是否必须 `Respond()`**——协议上无需应答，但 WinRT 的实际行为要在真机上确认。
3. **`MaxPduSize` 的实际协商值**——P1 记录里没有具体数字。真机读到后记进本文，作为后续性能估算的依据。
4. **Windows 作为外设时的实际通知速率**——`NotifyValueAsync` 的吞吐受协议栈限制，PRD 记的 9–20 KB/s 是射频层数据，应用层还要减去 ATT 开销。

这四项都不要在编码阶段"先猜一个"，猜错的代价是联调时把 bug 归因到错误的地方。

---

## 10. 编码期修正与留白答案（2026-11-01）

`BleGattPeripheralChannel` 落地时逐条核对了 §9，结论如下。
凡与初稿冲突的，**以本节为准**，初稿相应位置已就地标注。

### 10.1 §3 的 `InsufficientResources` 不存在

`GattCommunicationStatus` 只有四个值：`Success` / `Unreachable` / `ProtocolError` / `AccessDenied`。
初稿把 `BluetoothError` 家族的命名套用到了这里。实现改为：

| 返回值 | 处置 |
|---|---|
| `Success` | 继续下一块 |
| `Unreachable` | 退避 10 ms 重试，上限 20 次（约 200 ms）——可能是瞬断，也可能是协议栈暂时收不下 |
| `ProtocolError` / `AccessDenied` | 直接断链，不重试 |

真正的背压表现为 `NotifyValueAsync` 迟迟不返回，`await` 本身就是流控，不需要额外代码。

### 10.2 留白 1 → 用 `SubscribedClientsChanged`

P1 实测（BleProbe）已确认该事件能可靠反映中心连上/断开，不需要再找 `GattSession`。

**关键判定条件：从 >0 变成 0 才算断开。** 单看「当前为 0」是错的——
中心可能连上了但还没点订阅，那不是断开。`BleGattPeripheralChannel.ClientDisconnected`
按此条件触发，并且**不自动关闭通道**（重连策略归 T3.3）。

### 10.3 留白 2 → 默认应答，但保留退路

证据冲突：

| 来源 | 结论 |
|---|---|
| WinRT 文档 | 写请求必须调用 `Respond()` 才算处理完成 |
| P1 实测（BleProbe） | 只在 `WriteWithResponse` 时调用，iPhone 写入照样成功 |

取**更符合契约**的一侧：`AlwaysRespondToWrite` 默认 `true`。
应答失败只记日志、不中断链路——数据已经进 inbox，应答是另一条路径上的事。

### 10.4 留白 3 → `GattWriteRequestedEventArgs.Session` 是唯一入口

外设侧能拿 MTU 的唯一途径就是写入事件的 `Session` 属性，**中心发来第一块数据之前读不到**。
所以：

- 发送侧 `BleGattFraming.PayloadSizeFor(0)` 按 23 兜底 → 20 B，永远正确但保守；
- 首次写入时读到实际值并订阅 `MaxPduSizeChanged`，之后自动跟随；
- 实际协商值待真机验证后补到本节。

### 10.5 新增发现：§4「回调全同步」要打个补丁

初稿说接收路径「三步全部同步完成」。实际做不到——`GetRequestAsync()` 是 WinRT 规定的取
request 对象的方式，**必须 await**。

但 §4 的**本意**是对的，正确落地方式是：

```csharp
void OnWriteRequested(...)          // 系统线程，立刻返回
{
    var deferral = args.GetDeferral();
    _ = HandleWriteAsync(args, deferral);   // 起后台任务
}

async Task HandleWriteAsync(...)
{
    var request = await args.GetRequestAsync().AsTask().ConfigureAwait(false);  // ← 必须
    // 拿到 request 之后：拷字节、TryPost、Respond，全部同步，不 await 任何自家代码
}
```

**`ConfigureAwait(false)` 是硬要求**：WPF 下有 `SynchronizationContext`，
少了它续行会回到 UI 线程，而这段代码每来一块数据就跑一遍 —— 直接把界面拖死。
这个坑在控制台工具（BleProbe）里不会暴露，因为控制台没有 `SynchronizationContext`。

### 10.6 留白 4 与 §8 验收项 1–7 仍未验证

通知速率、实际 MTU、以及 §8 那张表里的全部条目，都还是**未验证状态**。
单测只能覆盖 `BleGattPeripheralOptions` 默认值与失败分类，`StartAsync` 必须有真机。

### 10.7 ★ §5 的广播方案装不下 —— 实测确认

真机跑起来第一眼就看到：

```
[通道] 广播状态 → Aborted
[通道] 广播状态 → StartedWithoutAllAdvertisementData
```

`StartedWithoutAllAdvertisementData` 不报错，只是**悄悄少发一部分数据**。算一遍预算就清楚：

| 广播内容 | 占用 |
|---|---|
| Flags AD（1 长度 + 1 类型 + 1 数据） | 3 B |
| 128 位 Service UUID 列表（1 + 1 + 16） | 18 B |
| **小计（不含 ServiceData）** | **21 B** |
| Service Data − 128 位 UUID（1 + 1 + **16** + 18） | **36 B** |
| 合计 | 57 B |

Legacy Advertising 的 PDU 有效载荷上限是 **31 B**，装 57 B 是不可能的。

**结论**：只要服务 UUID 是 128 位，就**不可能**同时携带 ServiceData —— 后者光自己的
UUID 前缀就吃掉 16 B。§5「Service UUID 同时放进广播数据」这句在物理上不成立。

**当前处置**（不改接口，先让问题可见）：

- 服务 UUID 仍在广播里（21 B < 31 B），所以**按 UUID 发现照常工作**，FR-03 不受影响；
- `ServiceData` 被系统丢弃，代码在启动后读一次 `AdvertisementStatus` 并给出明确日志，
  而不是让调用方对着 `Aborted` 猜；
- 竞选键改从 **META 特征值**读取（连接建立后一次 read），META 本来就是为这个准备的。

**待办（2026-11-23 已拍板）**：PRD §7.4.2 已按「靠服务 UUID 发现 + 连接后读 META」修订
（PRD v1.4），§7.4.1 选举时机同步改为「连接后选举 + 拓扑不符换边」。
`DiscoveryPayload` 18 B 线格式与 `RoleElection` 纯逻辑不变，T3.2 测试继续有效；
「换边重连」的连接层细节归 T3.3 设计文档。

### 10.8 广播状态是异步落定的

`StartAdvertising()` 返回时读 `AdvertisementStatus` 得到的是**瞬态 `Aborted`**，
约 80–100 ms 后才落到 `Started` / `StartedWithoutAllAdvertisementData`。

第一版代码在 `StartAdvertising` 之后立刻读状态并把它解释成「广播被中止，本机无法被搜到」——
**这是误报**，会把正常启动过程当成故障，让联调时往错误方向排查。

修法：等 200 ms 再读最终状态；同时把 `Aborted` 的措辞改成「启动瞬态，若 200 ms 后仍停在这里才是真失败」。


