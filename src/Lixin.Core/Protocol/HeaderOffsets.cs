namespace Lixin.Core.Protocol;

/// <summary>
/// 帧头各字段的字节偏移。线格式（PRD §7.5）：
/// MAGIC(2) | VER(1) | TYPE(1) | FLAGS(1) | SEQ(4,BE) | LEN(2,BE) | TAGLEN(1) | PAYLOAD | TAG
///
/// 这个类存在的唯一理由：消除魔法数字。此前 <see cref="FrameCodec.Encode"/>、
/// <see cref="FrameCodec.TryParse"/>、<see cref="FrameReader"/> 三处各自硬编码
/// <c>[2]/[3]/[4]/AsSpan(5)/AsSpan(9)/[11]</c>，改一次帧头布局要同步动 3 个文件约 8 处。
/// 现在布局只有这一个来源，<see cref="HeaderOffsetsTests"/> 保证它与 <see cref="ProtocolConstants.HeaderSize"/> 自洽。
/// </summary>
public static class HeaderOffsets
{
    /// <summary>MAGIC，2 字节，ASCII "LC"。</summary>
    public const int Magic = 0;

    /// <summary>协议版本，1 字节。</summary>
    public const int Version = Magic + 2;

    /// <summary>帧类型，1 字节。</summary>
    public const int Type = Version + 1;

    /// <summary>标志位，1 字节。</summary>
    public const int Flags = Type + 1;

    /// <summary>序号，4 字节，大端。</summary>
    public const int Sequence = Flags + 1;

    /// <summary>载荷长度，2 字节，大端。</summary>
    public const int PayloadLength = Sequence + 4;

    /// <summary>认证标签长度，1 字节。只允许 0 或 <see cref="ProtocolConstants.AuthTagSize"/>。</summary>
    public const int TagLength = PayloadLength + 2;

    /// <summary>载荷起始偏移，等于帧头总长 <see cref="ProtocolConstants.HeaderSize"/>。</summary>
    public const int Payload = TagLength + 1;
}
