# T3.4 设计：环境自检 + 可操作错误提示

> 状态：编码完成，手动关蓝牙的验收待做（验收判据：**手动关蓝牙能正确引导**）
> WBS：T3.4（1 人日）｜ 依赖：T3.1 的失败分类（`BleGattFailureReason`）

## 1. 职责切分（与 T3.3 同一条原则：决策与探测分离）

```
Lixin.Core/Environment/BleEnvironmentReport.cs   纯逻辑：原始结果 → 报告+提示（全单测）
Lixin.Transport/BleEnvironmentProbe.cs           平台探测：WinRT 采集，失败降级 Unknown，绝不抛
Lixin.Transport/BleGattEnvironment.cs            启动时检查：BluetoothOff 异常带 SettingsUri
```

- 报告模型不碰任何平台 API → 5 项规则全部可单测（7 条）。
- 探针只采集不判断 → 「蓝牙关了该说什么话」只写在一个地方。

## 2. 检查项与阻断语义（刻意与通道启动对齐）

| 项 | 阻断？ | NotReady 提示 |
|---|---|---|
| adapter 适配器 | ✅ | 笔记本查硬件禁用；台式机插 USB 蓝牙适配器 |
| ble BLE 支持 | ✅ | 离信依赖 BLE，换适配器 |
| radio 蓝牙开关 | ✅ | `设置 → 蓝牙和其他设备` 或唤起 `ms-settings:bluetooth` |
| radio 查询失败 | ❌ | 与 `EnsureRadioOnAsync` 的「查询失败继续尝试」同一语义（未打包进程常见） |
| peripheralRole / centralRole | ❌ | 信息项。角色自适应（D1）本来就是为能力不对等设计的；外设不支持会写入能力档案 |

- `IsReady` = 三个阻断项全过；Radio Unknown 不算 NotReady。
- `SettingsUri` 仅当开关关着 = `ms-settings:bluetooth`（UI 拿它直接唤起设置页）。
- 适配器缺失时**不**给设置页 URI——没有适配器时引导开蓝牙反而误导。

## 3. 启动路径上的可操作错误（T3.4 的另一半）

`BleGattStartupException` 新增 `SettingsUri`（init-only）。目前只有
`EnsureRadioOnAsync` 的 `BluetoothOff` 分支填 `ms-settings:bluetooth`；
其余失败原因（扫描超时、连接失败、订阅失败等）没有对应设置页，保持 null。
失败原因 → 提示文案的映射继续留在各抛出点，不在 T3.4 二次抽象。

## 4. 验收

| # | 判据 | 状态 |
|---|---|---|
| 1 | 手动关蓝牙后启动通道 → 异常带 `SettingsUri`，提示可操作 | 待做（真机） |
| 2 | 开着蓝牙 → 自检报告 IsReady、无 URI | 待做（真机） |
| 3 | 报告模型规则（含 Radio 未知、角色不支持不阻断） | ✅ 7 条单测 |
| 4 | 探针「任何环境不抛出 + 报告结构完整」 | ✅ 2 条单测 |
