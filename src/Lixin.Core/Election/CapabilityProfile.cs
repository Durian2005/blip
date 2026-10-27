namespace Lixin.Core.Election;

/// <summary>
/// 能力档案：运行时学习结果的持久化（PRD §7.4.1 规则 3）。
///
/// 关键认知：**外设角色能不能用，无法编译期或静态断定**（P1a 实测
/// `IsPeripheralRoleSupported` 可以静态查，但"能启动广播"和"广播真的可连接"仍是两回事），
/// 所以档案存的是**运行时试出来的结论**，不是配置常量。
///
/// 持久化在 `%LOCALAPPDATA%\Lixin\capability-profile.json`，
/// 其中 <see cref="DeviceId"/> 兼作角色选举的竞选键——身份和竞选键是同一个东西，
/// 换身份等于换一个人，选举结果就会翻过来，所以它必须持久化。
/// </summary>
public sealed class CapabilityProfile
{
    /// <summary>档案格式版本。结构不兼容时由存储层作废重建。</summary>
    public const int CurrentVersion = 1;

    /// <summary>格式版本。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>本机持久身份（16 字节随机数），兼作角色选举的竞选键。</summary>
    public byte[] DeviceId { get; set; } = new byte[CampaignKey.Size];

    /// <summary>外设角色已被实测判定不可用：此后启动直接当中心，不再重复试错。</summary>
    public bool PeripheralUnsupported { get; set; }

    /// <summary>判定不可用时的原因（HRESULT / 异常名），只用于诊断展示。</summary>
    public string? PeripheralFailureReason { get; set; }

    /// <summary>判定时间。配合人工排查用。</summary>
    public DateTimeOffset? PeripheralUnsupportedAt { get; set; }

    /// <summary>
    /// 记录一次外设角色失败。失败是**持久**的（PRD 明确"不再重复试错"）；
    /// 若日后硬件/驱动变化，用 <see cref="ClearPeripheralUnsupported"/> 显式清除或直接删档案文件。
    /// </summary>
    public void MarkPeripheralUnsupported(string reason)
    {
        PeripheralUnsupported = true;
        PeripheralFailureReason = reason;
        PeripheralUnsupportedAt = DateTimeOffset.Now;
    }

    /// <summary>显式清除失败记录（例如用户换了蓝牙适配器之后手动触发重试）。</summary>
    public void ClearPeripheralUnsupported()
    {
        PeripheralUnsupported = false;
        PeripheralFailureReason = null;
        PeripheralUnsupportedAt = null;
    }

    /// <summary>本机实际可用的能力组合：档案里的失败记录会关掉外设位。</summary>
    public RoleCapabilities EffectiveCapabilities(bool adapterSupportsPeripheral, bool adapterSupportsCentral)
        => new(
            CanAdvertise: adapterSupportsPeripheral && !PeripheralUnsupported,
            CanConnect: adapterSupportsCentral);
}
