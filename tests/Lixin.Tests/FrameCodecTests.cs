using System.Buffers.Binary;
using Lixin.Core.Protocol;

namespace Lixin.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_HeaderLayout_MatchesSpec()
    {
        var payload = "hello"u8.ToArray();
        var frame = Frame.Create(FrameType.Text, 0x01020304, payload, lastFragment: true);

        var bytes = FrameCodec.Encode(frame);

        Assert.Equal(ProtocolConstants.HeaderSize + payload.Length, bytes.Length);
        Assert.Equal(ProtocolConstants.Magic, BinaryPrimitives.ReadUInt16BigEndian(bytes));
        Assert.Equal(ProtocolConstants.Version, bytes[2]);
        Assert.Equal((byte)FrameType.Text, bytes[3]);
        Assert.Equal((byte)FrameFlags.LastFragment, bytes[4]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
        Assert.Equal((ushort)payload.Length, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(9)));
        Assert.Equal(0, bytes[11]);
        Assert.Equal(payload, bytes.AsSpan(ProtocolConstants.HeaderSize).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(ProtocolConstants.MaxPayload)]
    public void Roundtrip_AnySize_RestoresPayload(int size)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        var frame = Frame.Create(FrameType.Text, 7, payload);

        var bytes = FrameCodec.Encode(frame);
        var ok = FrameCodec.TryParse(bytes, out var parsed, out var consumed);

        Assert.True(ok);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(FrameType.Text, parsed.Type);
        Assert.Equal(7u, parsed.Sequence);
        Assert.Equal(payload, parsed.Payload.ToArray());
        Assert.Empty(parsed.AuthTag.ToArray());
    }

    [Fact]
    public void Encode_OverMaxPayload_Throws()
    {
        var payload = new byte[ProtocolConstants.MaxPayload + 1];
        var frame = Frame.Create(FrameType.Text, 0, payload);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCodec.Encode(frame));
    }

    [Fact]
    public void TryParse_IncompleteHeader_ReturnsFalse()
    {
        var frame = Frame.Create(FrameType.Text, 1, "abc"u8.ToArray());
        var bytes = FrameCodec.Encode(frame);

        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.False(FrameCodec.TryParse(bytes.AsSpan(0, length), out _, out var consumed));
            Assert.Equal(0, consumed);
        }
    }

    [Fact]
    public void TryParse_AuthTagLength_IsRespected()
    {
        var frame = new Frame(ProtocolConstants.Version, FrameType.Handshake, FrameFlags.None, 3,
            "data"u8.ToArray(), new byte[ProtocolConstants.AuthTagSize]);

        var bytes = FrameCodec.Encode(frame);
        Assert.Equal(ProtocolConstants.AuthTagSize, bytes[11]);

        Assert.True(FrameCodec.TryParse(bytes, out var parsed, out var consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(ProtocolConstants.AuthTagSize, parsed.AuthTag.Length);
    }

    // ---------- H2：TAGLEN 校验（2026-10-31 补，P4 接 AEAD 前的安全前置项） ----------
    // 线格式只允许两种 TAGLEN：0（P2 明文）与 AuthTagSize（P4 起密文）。
    // 其余值意味着字节流已错位或对端在发垃圾——绝不能当"数据不够"继续等，
    // 也绝不能把这个帧解出来交给上层。

    /// <summary>手工构造一个帧字节串，用于绕过 Encode 制造畸形输入。</summary>
    private static byte[] CraftFrame(int tagLength, byte[] payload)
    {
        var bytes = new byte[ProtocolConstants.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(HeaderOffsets.Magic), ProtocolConstants.Magic);
        bytes[HeaderOffsets.Version] = ProtocolConstants.Version;
        bytes[HeaderOffsets.Type] = (byte)FrameType.Text;
        bytes[HeaderOffsets.Flags] = (byte)FrameFlags.LastFragment;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(HeaderOffsets.Sequence), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(HeaderOffsets.PayloadLength), (ushort)payload.Length);
        bytes[HeaderOffsets.TagLength] = (byte)tagLength;
        payload.CopyTo(bytes, HeaderOffsets.Payload);
        return bytes;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ProtocolConstants.AuthTagSize)]
    public void Encode_KnownAuthTagLengths_Accepted(int tagLength)
    {
        var frame = new Frame(ProtocolConstants.Version, FrameType.Text, FrameFlags.LastFragment, 1,
            "x"u8.ToArray(), new byte[tagLength]);
        Assert.Equal(ProtocolConstants.HeaderSize + 1 + tagLength, FrameCodec.Encode(frame).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(255)]
    public void Encode_UnknownAuthTagLength_Throws(int tagLength)
    {
        var frame = new Frame(ProtocolConstants.Version, FrameType.Text, FrameFlags.LastFragment, 1,
            "x"u8.ToArray(), new byte[tagLength]);
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(frame));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(255)]
    public void TryParse_UnknownAuthTagLength_ReturnsFalse(int tagLength)
    {
        var bytes = CraftFrame(tagLength, "abc"u8.ToArray());
        Assert.False(FrameCodec.TryParse(bytes, out _, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ZeroAuthTagLength_StillParses()
    {
        var bytes = CraftFrame(0, "abc"u8.ToArray());
        Assert.True(FrameCodec.TryParse(bytes, out var parsed, out var consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(parsed.Payload.Span));
    }
}
