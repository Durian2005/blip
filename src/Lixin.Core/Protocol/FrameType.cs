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

/// <summary>
/// 帧类型的传输语义分类。
/// 这条分类决定一个帧要不要进分片重组器，是协议层面的规则，不属于任何一方的实现细节。
/// </summary>
public static class FrameTypes
{
    /// <summary>
    /// 是否属于「可分片的数据消息」。
    ///
    /// - 返回 <c>true</c>：走 <see cref="MessageAssembler"/> 组装，其序号必须在同一条消息内连续。
    /// - 返回 <c>false</c>：按**单帧控制帧**处理，直接交付，不参与重组，
    ///   也**不占用数据帧的序号空间**（否则会在正文中间插出一个序号空洞，把正文判成断裂）。
    ///
    /// ★ 新增帧类型时必须在此登记。默认归为控制帧，宁可被立即交付，也不要被误当成正文的分片。
    /// </summary>
    public static bool IsFragmented(FrameType type) => type is FrameType.Text;
}
