namespace Lixin.Transport;

/// <summary>
/// BLE GATT 的 UUID 常量。
///
/// ⚠️ 这些值**一旦写死就不要再改**。原因不是代码问题，而是 iOS 的行为：
/// iPhone 会按设备缓存对端的 GATT 表，改了 UUID 之后手机侧仍拿着旧表，
/// 表现为「新特征值凭空消失」，只能靠「忽略此设备」或重启 iPhone 才能恢复。
/// 调试期反复改 UUID 是 T3.1 最容易白丢半天的地方，所以宁可一次定死。
///
/// 命名规则：前缀 <c>6c697869-6e00</c> 是 "lixin" 的 ASCII（6c 69 78 69 6e），
/// 抓包时人眼一眼能认出是自己的流量。
/// 后缀刻意避开 Bluetooth SIG 的保留基础后缀 <c>00805f9b34fb</c>，
/// 免得和官方定义的 16 位 UUID 语义混淆。
/// </summary>
public static class BleGattUuids
{
    /// <summary>主服务：广播时按它过滤（FR-03 的发现口径）。</summary>
    public static readonly Guid Service = new("6c697869-6e00-4f1a-9c3d-1a2b3c4d5e60");

    /// <summary>数据方向：中心 → 外设。属性 = Write / WriteWithoutResponse。</summary>
    public static readonly Guid Tx = new("6c697869-6e01-4f1a-9c3d-1a2b3c4d5e61");

    /// <summary>数据方向：外设 → 中心。属性 = Notify，中心需订阅。</summary>
    public static readonly Guid Rx = new("6c697869-6e02-4f1a-9c3d-1a2b3c4d5e62");

    /// <summary>元信息：协议版本、昵称、能力位。属性 = Read。替代把身份塞进广播的必要性。</summary>
    public static readonly Guid Meta = new("6c697869-6e03-4f1a-9c3d-1a2b3c4d5e63");

    /// <summary>全部 UUID，测试用来验证没有重复（复制粘贴打错一位是经典事故）。</summary>
    public static IReadOnlyList<Guid> All { get; } = new[] { Service, Tx, Rx, Meta };
}
