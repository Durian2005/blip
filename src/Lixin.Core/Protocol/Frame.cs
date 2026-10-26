namespace Lixin.Core.Protocol;

/// <summary>
/// 一个协议帧。线格式：
/// MAGIC(2) | VER(1) | TYPE(1) | FLAGS(1) | SEQ(4,BE) | LEN(2,BE) | TAGLEN(1) | PAYLOAD(LEN) | TAG(TAGLEN)
/// </summary>
/// <param name="Version">协议版本。</param>
/// <param name="Type">帧类型。</param>
/// <param name="Flags">标志位。</param>
/// <param name="Sequence">序号，同一逻辑消息的分片连续递增。</param>
/// <param name="Payload">载荷。P4 起为密文。</param>
/// <param name="AuthTag">AEAD 认证标签。P2 阶段为空。</param>
public readonly record struct Frame(
    byte Version,
    FrameType Type,
    FrameFlags Flags,
    uint Sequence,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> AuthTag)
{
    /// <summary>是否是最后一片。</summary>
    public bool IsLastFragment => (Flags & FrameFlags.LastFragment) != 0;

    /// <summary>构造一个未加密帧（P2 用）。</summary>
    public static Frame Create(FrameType type, uint sequence, ReadOnlyMemory<byte> payload, bool lastFragment = true)
        => new(ProtocolConstants.Version, type, lastFragment ? FrameFlags.LastFragment : FrameFlags.None,
               sequence, payload, ReadOnlyMemory<byte>.Empty);
}
