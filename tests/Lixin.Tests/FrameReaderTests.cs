using Lixin.Core.Protocol;

namespace Lixin.Tests;

public class FrameReaderTests
{
    private static byte[] Encode(FrameType type, uint sequence, string text, bool last = true)
        => FrameCodec.Encode(Frame.Create(type, sequence, System.Text.Encoding.UTF8.GetBytes(text), last));

    [Fact]
    public void ByteByByte_StillYieldsWholeFrame()
    {
        var bytes = Encode(FrameType.Text, 1, "half-packet");
        var reader = new FrameReader();

        for (var i = 0; i < bytes.Length - 1; i++)
        {
            reader.Append(bytes.AsSpan(i, 1));
            Assert.False(reader.TryRead(out _));  // 还差最后几个字节，不应提前吐帧
        }

        reader.Append(bytes.AsSpan(bytes.Length - 1, 1));
        Assert.True(reader.TryRead(out var frame));
        Assert.Equal("half-packet", System.Text.Encoding.UTF8.GetString(frame.Payload.Span));
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void StickyPacket_TwoFramesInOneAppend()
    {
        var first = Encode(FrameType.Text, 1, "first");
        var second = Encode(FrameType.Ack, 2, "second");
        var combined = first.Concat(second).ToArray();

        var reader = new FrameReader();
        reader.Append(combined);

        Assert.True(reader.TryRead(out var a));
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(a.Payload.Span));
        Assert.True(reader.TryRead(out var b));
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(b.Payload.Span));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void GarbagePrefix_IsSkippedAndCounted()
    {
        var garbage = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var frame = Encode(FrameType.Text, 9, "after-garbage");

        var reader = new FrameReader();
        reader.Append(garbage);
        reader.Append(frame);

        Assert.True(reader.TryRead(out var parsed));
        Assert.Equal("after-garbage", System.Text.Encoding.UTF8.GetString(parsed.Payload.Span));
        Assert.Equal(garbage.Length, reader.DiscardedBytes);
    }

    [Fact]
    public void OversizedLength_DoesNotHang_AndResyncs()
    {
        // 伪造一个 LEN = 0xFFFF 的坏帧头，后面紧跟一个合法帧
        var bogus = new byte[ProtocolConstants.HeaderSize];
        bogus[0] = (byte)(ProtocolConstants.Magic >> 8);
        bogus[1] = (byte)(ProtocolConstants.Magic & 0xFF);
        bogus[2] = ProtocolConstants.Version;
        bogus[3] = (byte)FrameType.Text;
        bogus[9] = 0xFF;
        bogus[10] = 0xFF;

        var good = Encode(FrameType.Text, 5, "recovered");
        var reader = new FrameReader();
        reader.Append(bogus);
        reader.Append(good);

        Assert.True(reader.TryRead(out var parsed));
        Assert.Equal("recovered", System.Text.Encoding.UTF8.GetString(parsed.Payload.Span));
        Assert.True(reader.DiscardedBytes > 0);
    }

    [Fact]
    public void PartialHeader_ReturnsFalseWithoutConsuming()
    {
        var frame = Encode(FrameType.Text, 1, "abc");
        var reader = new FrameReader();
        reader.Append(frame.AsSpan(0, 5));

        Assert.False(reader.TryRead(out _));
        Assert.Equal(5, reader.BufferedBytes);
        Assert.Equal(0, reader.DiscardedBytes);
    }
}
