using System.Buffers.Binary;

namespace Lixin.Core.Protocol;

/// <summary>单帧编解码。纯函数，无状态，便于单测。</summary>
public static class FrameCodec
{
    /// <summary>把一个帧编码成字节数组。</summary>
    public static byte[] Encode(in Frame frame)
    {
        if (frame.Payload.Length > ProtocolConstants.MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"载荷 {frame.Payload.Length} 字节超过单帧上限 {ProtocolConstants.MaxPayload}。应先分片。");
        }

        var tagLength = frame.AuthTag.Length;
        var total = ProtocolConstants.HeaderSize + frame.Payload.Length + tagLength;
        var buffer = new byte[total];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, ProtocolConstants.Magic);
        buffer[2] = frame.Version;
        buffer[3] = (byte)frame.Type;
        buffer[4] = (byte)frame.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5), frame.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(9), (ushort)frame.Payload.Length);
        buffer[11] = (byte)tagLength;

        frame.Payload.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));
        frame.AuthTag.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize + frame.Payload.Length));

        return buffer;
    }

    /// <summary>
    /// 尝试从缓冲区开头解析一个完整帧。
    /// 返回 false 表示「数据还不够」（consumed 恒为 0）；魔数不对也返回 false，由调用方负责重新同步。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;

        if (source.Length < ProtocolConstants.HeaderSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(source) != ProtocolConstants.Magic)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(source[9..]);
        var tagLength = source[11];
        var total = ProtocolConstants.HeaderSize + payloadLength + tagLength;

        if (source.Length < total)
        {
            return false;
        }

        frame = new Frame(
            Version: source[2],
            Type: (FrameType)source[3],
            Flags: (FrameFlags)source[4],
            Sequence: BinaryPrimitives.ReadUInt32BigEndian(source[5..]),
            Payload: source.Slice(ProtocolConstants.HeaderSize, payloadLength).ToArray(),
            AuthTag: source.Slice(ProtocolConstants.HeaderSize + payloadLength, tagLength).ToArray());

        consumed = total;
        return true;
    }
}
