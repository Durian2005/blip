using Lixin.Core.Protocol;

namespace Lixin.Tests;

/// <summary>
/// <see cref="HeaderOffsets"/> 的自洽性守卫（对应审查 M3）。
/// 帧头布局从此只有一个来源；这三个测试保证任何"改布局忘改常量"的行为立即红灯。
/// </summary>
public class HeaderOffsetsTests
{
    /// <summary>六个字段必须严丝合缝地铺满 12 字节帧头，无空洞、无重叠。</summary>
    [Fact]
    public void Offsets_TileTheHeader_WithoutGapsOrOverlap()
    {
        Assert.Equal(0, HeaderOffsets.Magic);
        Assert.Equal(HeaderOffsets.Magic + 2, HeaderOffsets.Version);          // MAGIC 2 B
        Assert.Equal(HeaderOffsets.Version + 1, HeaderOffsets.Type);           // VER 1 B
        Assert.Equal(HeaderOffsets.Type + 1, HeaderOffsets.Flags);             // TYPE 1 B
        Assert.Equal(HeaderOffsets.Flags + 1, HeaderOffsets.Sequence);         // FLAGS 1 B
        Assert.Equal(HeaderOffsets.Sequence + 4, HeaderOffsets.PayloadLength); // SEQ 4 B
        Assert.Equal(HeaderOffsets.PayloadLength + 2, HeaderOffsets.TagLength);// LEN 2 B
        Assert.Equal(HeaderOffsets.TagLength + 1, HeaderOffsets.Payload);      // TAGLEN 1 B
        Assert.Equal(HeaderOffsets.Payload, ProtocolConstants.HeaderSize);
    }

    /// <summary>Encode 实际写出的字节必须落在偏移常量声名的位置上。</summary>
    [Fact]
    public void Encode_WritesFields_AtDeclaredOffsets()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var frame = new Frame(ProtocolConstants.Version, FrameType.Handshake, FrameFlags.LastFragment,
            0x0A0B0C0D, payload, new byte[ProtocolConstants.AuthTagSize]);

        var bytes = FrameCodec.Encode(frame);

        Assert.Equal(ProtocolConstants.Magic, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
            bytes.AsSpan(HeaderOffsets.Magic, 2)));
        Assert.Equal(ProtocolConstants.Version, bytes[HeaderOffsets.Version]);
        Assert.Equal((byte)FrameType.Handshake, bytes[HeaderOffsets.Type]);
        Assert.Equal((byte)FrameFlags.LastFragment, bytes[HeaderOffsets.Flags]);
        Assert.Equal(0x0A0B0C0Du, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(HeaderOffsets.Sequence, 4)));
        Assert.Equal((ushort)payload.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
            bytes.AsSpan(HeaderOffsets.PayloadLength, 2)));
        Assert.Equal(ProtocolConstants.AuthTagSize, bytes[HeaderOffsets.TagLength]);
        Assert.Equal(payload, bytes.AsSpan(HeaderOffsets.Payload, payload.Length).ToArray());
    }
}
