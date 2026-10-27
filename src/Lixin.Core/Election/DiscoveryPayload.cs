namespace Lixin.Core.Election;

/// <summary>
/// 广播发现载荷：塞进 BLE 广播的厂商自定义数据里，是双方**在建立连接之前**交换的唯一信息。
///
/// 线格式（共 18 字节，必须同步 PRD §7.4.1）：
/// <code>
/// BYTE 0      : 版本，恒 0x01
/// BYTE 1      : 能力位。bit0 = 可当外设（可连接广播），bit1 = 可当中心（可发起连接）
/// BYTE 2..17  : 竞选键，16 字节，原样二进制（不做 Base64，省空间）
/// </code>
///
/// 18 字节放进厂商数据（Legacy 广播 31 字节上限内绰绰有余）。
/// 公司 ID 由传输层选（T3.1），本类型只负责载荷本身的编解码。
/// </summary>
public static class DiscoveryPayload
{
    /// <summary>载荷版本。改它 = 破坏兼容。</summary>
    public const byte Version = 0x01;

    /// <summary>总长度：版本 1 + 能力位 1 + 竞选键 16。</summary>
    public const int Size = 18;

    [Flags]
    private enum CapabilityBits : byte
    {
        None = 0,
        CanAdvertise = 1 << 0,
        CanConnect = 1 << 1,
    }

    /// <summary>编码一个发现载荷。</summary>
    public static byte[] Encode(CampaignKey key, RoleCapabilities capabilities)
    {
        var buffer = new byte[Size];
        buffer[0] = Version;
        buffer[1] = ToBits(capabilities);
        key.Value.Span.CopyTo(buffer.AsSpan(2));
        return buffer;
    }

    /// <summary>
    /// 尝试解码。版本不符、长度不足、能力位非法都返回 false——
    /// 扫描时会把附近所有设备的广播都喂进来，失败是常态而不是异常。
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out CampaignKey key,
        out RoleCapabilities capabilities)
    {
        key = default;
        capabilities = default;

        if (source.Length != Size) return false;
        if (source[0] != Version) return false;

        var bits = (CapabilityBits)source[1];

        // 出现未知能力位说明对端版本更新，按「不认识 = 不支持」处理，避免误判
        if ((bits & ~(CapabilityBits.CanAdvertise | CapabilityBits.CanConnect)) != 0) return false;

        var keyBytes = source.Slice(2, CampaignKey.Size).ToArray();
        key = CampaignKey.FromBytes(keyBytes);
        capabilities = new RoleCapabilities(
            CanAdvertise: bits.HasFlag(CapabilityBits.CanAdvertise),
            CanConnect: bits.HasFlag(CapabilityBits.CanConnect));
        return true;
    }

    private static byte ToBits(RoleCapabilities capabilities)
    {
        var bits = CapabilityBits.None;
        if (capabilities.CanAdvertise) bits |= CapabilityBits.CanAdvertise;
        if (capabilities.CanConnect) bits |= CapabilityBits.CanConnect;
        return (byte)bits;
    }
}
