namespace Lixin.Core.Protocol;

/// <summary>帧类型。低 4 位是类别，高 4 位留作细分。</summary>
public enum FrameType : byte
{
    /// <summary>握手前的明文问候：版本、昵称、能力位。</summary>
    Hello = 0x01,

    /// <summary>X25519 公钥交换（P4 起使用）。</summary>
    Handshake = 0x02,

    /// <summary>SAS 验证结果（P4 起使用）。</summary>
    SasConfirm = 0x03,

    /// <summary>文本消息。</summary>
    Text = 0x10,

    /// <summary>送达回执。</summary>
    Ack = 0x11,

    /// <summary>已读回执。</summary>
    Read = 0x12,

    /// <summary>保活探测。</summary>
    Ping = 0x13,

    /// <summary>保活应答。</summary>
    Pong = 0x14,

    /// <summary>主动断开。</summary>
    Bye = 0xFF,
}

/// <summary>帧标志位。</summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0,

    /// <summary>这是同一条逻辑消息的最后一个分片。</summary>
    LastFragment = 1 << 0,
}
