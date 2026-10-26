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
}
