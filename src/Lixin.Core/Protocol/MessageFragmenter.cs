namespace Lixin.Core.Protocol;

/// <summary>把一条逻辑消息切片成若干帧。</summary>
public static class MessageFragmenter
{
    /// <summary>
    /// 从 <paramref name="startSequence"/> 开始，把 payload 切成多帧，最后一片带 LastFragment 标志。
    /// 序号会随分片递增，调用方应把返回的帧数累加进自己的序号计数器。
    /// </summary>
    public static IReadOnlyList<Frame> Fragment(
        FrameType type,
        uint startSequence,
        ReadOnlyMemory<byte> payload,
        int maxPayload = ProtocolConstants.MaxPayload)
    {
        if (maxPayload is <= 0 or > ProtocolConstants.MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayload),
                $"单帧载荷必须在 1..{ProtocolConstants.MaxPayload} 之间。");
        }

        if (payload.Length > ProtocolConstants.MaxMessageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"消息 {payload.Length} 字节超过上限 {ProtocolConstants.MaxMessageSize}。");
        }

        var frames = new List<Frame>();
        var offset = 0;
        var sequence = startSequence;

        // 空消息也要发一帧（例如空配置），保证对端有明确的事件
        if (payload.Length == 0)
        {
            frames.Add(Frame.Create(type, sequence, ReadOnlyMemory<byte>.Empty, lastFragment: true));
            return frames;
        }

        while (offset < payload.Length)
        {
            var length = Math.Min(maxPayload, payload.Length - offset);
            var isLast = offset + length >= payload.Length;
            frames.Add(Frame.Create(type, sequence, payload.Slice(offset, length), isLast));
            offset += length;
            sequence++;
        }

        return frames;
    }
}
