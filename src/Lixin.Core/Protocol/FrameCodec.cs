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

        // H2：TAGLEN 只允许 0（明文）与 AuthTagSize（密文）。
        // 放进来的帧带个 5 字节的"认证标签"，对端无法解析，是编码期的逻辑错误，就地拦住。
        var tagLength = frame.AuthTag.Length;
        if (tagLength != ProtocolConstants.NoAuthTag && tagLength != ProtocolConstants.AuthTagSize)
        {
            throw new ArgumentException(
                $"认证标签长度 {tagLength} 非法：只允许 0 或 {ProtocolConstants.AuthTagSize}。", nameof(frame));
        }

        var total = ProtocolConstants.HeaderSize + frame.Payload.Length + tagLength;
        var buffer = new byte[total];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(HeaderOffsets.Magic), ProtocolConstants.Magic);
        buffer[HeaderOffsets.Version] = frame.Version;
        buffer[HeaderOffsets.Type] = (byte)frame.Type;
        buffer[HeaderOffsets.Flags] = (byte)frame.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(HeaderOffsets.Sequence), frame.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(HeaderOffsets.PayloadLength), (ushort)frame.Payload.Length);
        buffer[HeaderOffsets.TagLength] = (byte)tagLength;

        frame.Payload.Span.CopyTo(buffer.AsSpan(HeaderOffsets.Payload));
        frame.AuthTag.Span.CopyTo(buffer.AsSpan(HeaderOffsets.Payload + frame.Payload.Length));

        return buffer;
    }

    /// <summary>
    /// 尝试从缓冲区开头解析一个完整帧。
    /// 返回 false 表示「数据还不够」或「帧非法」（consumed 恒为 0）——
    /// 魔数不对、TAGLEN 非法都走这条路，由调用方（<see cref="FrameReader"/>）负责重新同步。
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

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(HeaderOffsets.PayloadLength, 2));

        // H2：与 Encode 同一套取值口径。非法 TAGLEN 说明字节流已错位或对端在发垃圾，
        // 绝不能解出一个"看着完整"的帧交给上层。
        var tagLength = source[HeaderOffsets.TagLength];
        if (tagLength != ProtocolConstants.NoAuthTag && tagLength != ProtocolConstants.AuthTagSize)
        {
            return false;
        }

        var total = ProtocolConstants.HeaderSize + payloadLength + tagLength;

        if (source.Length < total)
        {
            return false;
        }

        frame = new Frame(
            Version: source[HeaderOffsets.Version],
            Type: (FrameType)source[HeaderOffsets.Type],
            Flags: (FrameFlags)source[HeaderOffsets.Flags],
            Sequence: BinaryPrimitives.ReadUInt32BigEndian(source.Slice(HeaderOffsets.Sequence, 4)),
            Payload: source.Slice(HeaderOffsets.Payload, payloadLength).ToArray(),
            AuthTag: source.Slice(HeaderOffsets.Payload + payloadLength, tagLength).ToArray());

        consumed = total;
        return true;
    }
}
