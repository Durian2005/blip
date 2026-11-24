namespace Lixin.Core.Env;

/// <summary>单项环境检查的结论。</summary>
public enum BleCheckStatus
{
    /// <summary>检查通过。</summary>
    Ready,

    /// <summary>检查不通过。是否阻断运行取决于具体项（角色不支持是信息项，不阻断）。</summary>
    NotReady,

    /// <summary>查不到（适配器缺失、无权限等）。与通道启动语义一致：查不到不等于不可用。</summary>
    Unknown,
}

/// <summary>环境自检的单项结论。</summary>
/// <param name="Id">机器可读的标识（adapter / ble / radio / peripheralRole / centralRole）。</param>
/// <param name="Title">给用户看的项名。</param>
/// <param name="Status">结论。</param>
/// <param name="Detail">补充说明（如观测到的具体值）。</param>
/// <param name="FixHint">NotReady 时给用户的可操作建议。</param>
public sealed record BleCheckItem(
    string Id,
    string Title,
    BleCheckStatus Status,
    string? Detail = null,
    string? FixHint = null);

/// <summary>
/// 环境自检报告（T3.4）。纯逻辑：输入原始探测结果，输出逐项结论 + 可操作提示。
/// 平台探测（WinRT）在 <c>Lixin.Transport/BleEnvironmentProbe</c>，这里不碰任何平台 API。
/// </summary>
/// <remarks>
/// 阻断语义与通道启动保持一致（这是刻意的，不是巧合）：
/// <list type="bullet">
/// <item>适配器缺失 / 不支持 BLE / 蓝牙开关关 —— <b>阻断</b>；</item>
/// <item>Radio 状态查不到（未打包进程常见）—— <b>不阻断</b>，与
/// <c>BleGattEnvironment.EnsureRadioOnAsync</c> 的「查询失败继续尝试」一致；</item>
/// <item>角色不支持 —— <b>不阻断</b>，角色自适应（D1）本来就是为能力不对等设计的。</item>
/// </list>
/// </remarks>
public sealed class BleEnvironmentReport
{
    private static readonly string _radioSettingsUri = "ms-settings:bluetooth";

    private BleEnvironmentReport(IReadOnlyList<BleCheckItem> items)
    {
        Items = items;
        IsReady = Items
            .Where(IsBlocker)
            .All(item => item.Status != BleCheckStatus.NotReady);
        SettingsUri = Items.Any(item =>
            item.Id == "radio"
            && item.Status == BleCheckStatus.NotReady)
            ? _radioSettingsUri
            : null;
        FirstBlockerHint = Items
            .Where(IsBlocker)
            .FirstOrDefault(item => item.Status == BleCheckStatus.NotReady)?.FixHint;
    }

    /// <summary>逐项检查结论，顺序固定：适配器 → BLE → 开关 → 外设角色 → 中心角色。</summary>
    public IReadOnlyList<BleCheckItem> Items { get; }

    /// <summary>是否可以启动（阻断项全部通过；Radio 未知不算阻断）。</summary>
    public bool IsReady { get; }

    /// <summary>
    /// 系统设置页 URI，仅当蓝牙开关关着时为 <c>ms-settings:bluetooth</c>，
    /// UI 层可以直接拿它唤起设置页（T3.4 的验收判据）。
    /// </summary>
    public string? SettingsUri { get; }

    /// <summary>第一个阻断项的可操作提示；全部通过时为 null。</summary>
    public string? FirstBlockerHint { get; }

    /// <summary>由原始探测结果构造报告。探测失败传 null / radioKnown=false，不要自己猜。</summary>
    public static BleEnvironmentReport Build(
        bool adapterPresent,
        bool? bleCapable,
        bool radioKnown,
        bool radioOn,
        bool? peripheralRoleSupported,
        bool? centralRoleSupported)
    {
        var items = new List<BleCheckItem>(5)
        {
            adapterPresent
                ? new BleCheckItem("adapter", "蓝牙适配器", BleCheckStatus.Ready)
                : new BleCheckItem("adapter", "蓝牙适配器", BleCheckStatus.NotReady,
                    FixHint: "没有找到蓝牙适配器。笔记本请确认硬件未被禁用；台式机需要插一个 USB 蓝牙适配器。"),
            BuildBleItem(adapterPresent, bleCapable),
            BuildRadioItem(adapterPresent, radioKnown, radioOn),
            BuildRoleItem("peripheralRole", "外设角色（可被连接）", peripheralRoleSupported,
                "本机适配器不支持外设角色，只能当中心主动连接对端。" +
                "此结论会写入能力档案，之后启动时直接走中心侧，不再重复试错。"),
            BuildRoleItem("centralRole", "中心角色（主动连接）", centralRoleSupported,
                "本机适配器不支持中心角色，只能发广播等对端来连。"),
        };

        return new BleEnvironmentReport(items);
    }

    private static BleCheckItem BuildBleItem(bool adapterPresent, bool? bleCapable) => !adapterPresent
        ? new BleCheckItem("ble", "BLE（低功耗）支持", BleCheckStatus.Unknown,
            Detail: "适配器缺失，无法判断。")
        : bleCapable switch
        {
            true => new BleCheckItem("ble", "BLE（低功耗）支持", BleCheckStatus.Ready),
            false => new BleCheckItem("ble", "BLE（低功耗）支持", BleCheckStatus.NotReady,
                FixHint: "本机适配器不支持低功耗蓝牙（BLE）。离信依赖 BLE，换一个支持 BLE 的适配器。"),
            null => new BleCheckItem("ble", "BLE（低功耗）支持", BleCheckStatus.Unknown,
                Detail: "能力查询失败，不阻断——真正的失败会在后续 GATT 操作里暴露。"),
        };

    private static BleCheckItem BuildRadioItem(bool adapterPresent, bool radioKnown, bool radioOn)
    {
        if (!adapterPresent)
        {
            return new BleCheckItem("radio", "蓝牙开关", BleCheckStatus.Unknown,
                Detail: "适配器缺失，无法判断。");
        }

        if (!radioKnown)
        {
            // 与 EnsureRadioOnAsync 的「查询失败不阻断」对齐：未打包的桌面进程可能无权查 Radio。
            return new BleCheckItem("radio", "蓝牙开关", BleCheckStatus.Unknown,
                Detail: "Radio 状态查询失败（常见于未打包进程），不阻断，继续尝试。");
        }

        return radioOn
            ? new BleCheckItem("radio", "蓝牙开关", BleCheckStatus.Ready)
            : new BleCheckItem("radio", "蓝牙开关", BleCheckStatus.NotReady,
                FixHint: "蓝牙没有打开。打开「设置 → 蓝牙和其他设备」的开关，" +
                    $"或直接唤起 {_radioSettingsUri}。");
    }

    private static BleCheckItem BuildRoleItem(string id, string title, bool? supported, string fixHint) =>
        supported switch
        {
            true => new BleCheckItem(id, title, BleCheckStatus.Ready),
            false => new BleCheckItem(id, title, BleCheckStatus.NotReady,
                Detail: "这是能力缺失，属于信息项，不阻断运行。",
                FixHint: fixHint),
            null => new BleCheckItem(id, title, BleCheckStatus.Unknown),
        };

    private static bool IsBlocker(BleCheckItem item) =>
        item.Id is "adapter" or "ble" or "radio";
}
