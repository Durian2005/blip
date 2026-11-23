namespace Lixin.Core.Election;

/// <summary>
/// 发现载荷：版本 + 能力位 + 竞选键，共 18 字节。
///
/// v1.4 修订（2026-11-01 真机实测驱动）：Legacy 广播 31 B 预算装不下
/// 「128 位 Service UUID AD（18 B）+ 本载荷（36 B）」，系统会悄悄丢弃 ServiceData
/// （StartedWithoutAllAdvertisementData，不报错）。因此载荷**不再放广播**，
/// 改放 META 特征值，连接建立后一次 read 获取；发现只靠广播里的 Service UUID。
/// 线格式本身与 v1.3 一致，必须同步 PRD §7.4.2。
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
