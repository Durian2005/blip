using Lixin.Transport;

namespace Lixin.Tests;

public class BleGattFramingTests
{
    [Theory]
    [InlineData(23, 20)]    // 规范默认 MTU：20 B 有效载荷，也是最保守的下限
    [InlineData(185, 182)]  // 常见的协商结果
    [InlineData(247, 244)]  // DLE 下的常见上限
    [InlineData(517, 512)]  // ATT_MTU 规范上限 → 被 MaxPayload 封顶
    [InlineData(1000, 512)] // 异常大的值同样封顶，防止写出超长 PDU
    public void PayloadSizeFor_NormalMtu_SubtractsAttOverhead(int mtu, int expected)
    {
        Assert.Equal(expected, BleGattFraming.PayloadSizeFor(mtu));
    }

    [Theory]
    [InlineData(0)]   // 还没取到 MaxPduSize
    [InlineData(-1)]  // 未初始化的 sentinel
    public void PayloadSizeFor_UnknownMtu_FallsBackToDefault(int mtu)
    {
        // 拿不到 MTU 时必须有一个「永远正确」的安全默认，而不是崩或返回 0。
        Assert.Equal(BleGattFraming.MinPayload, BleGattFraming.PayloadSizeFor(mtu));
    }

    [Fact]
    public void PayloadSizeFor_AbnormallySmallMtu_StillReturnsMinimum()
    {
        // mtu < 23 在规范里不可能出现。若真遇到，返回 20 而不是更小的数：
        // 20 是保证正确的保守值，返回更小的数会让写入永远失败、更难排查。
        Assert.Equal(BleGattFraming.MinPayload, BleGattFraming.PayloadSizeFor(5));
    }

    [Fact]
    public void Slice_EmptyData_ReturnsNoChunks()
    {
        // 不能返回「一个长度 0 的片段」，否则调用方会白发一次空写。
        Assert.Empty(BleGattFraming.Slice(ReadOnlyMemory<byte>.Empty, 20));
    }

    [Fact]
    public void Slice_SplitsIntoExpectedChunkCount()
    {
        var data = new byte[512];
        var chunks = BleGattFraming.Slice(data, 20);

        Assert.Equal(26, chunks.Count);          // 25 × 20 + 12
        Assert.Equal(20, chunks[0].Length);
        Assert.Equal(12, chunks[^1].Length);     // 尾块只装剩下的 12 B
    }

    [Fact]
    public void Slice_ExactMultiple_DoesNotProduceTrailingEmptyChunk()
    {
        var chunks = BleGattFraming.Slice(new byte[40], 20);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(20, c.Length));
    }

    [Theory]
    [InlineData(20)]   // 默认 MTU 下的最坏情况
    [InlineData(182)]
    [InlineData(244)]
    public void Slice_ReassembledData_IsByteIdentical(int payloadSize)
    {
        var random = new Random(20261030);
        var data = new byte[32 * 1024];   // 单条消息上限
        random.NextBytes(data);

        var chunks = BleGattFraming.Slice(data, payloadSize);

        var rebuilt = new byte[data.Length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Span.CopyTo(rebuilt.AsSpan(offset));
            offset += chunk.Length;
        }

        Assert.Equal(chunks.Count, BleGattFraming.ChunkCount(data.Length, payloadSize));
        Assert.Equal(data.Length, offset);
        Assert.Equal(data, rebuilt);
    }

    [Fact]
    public void Slice_ReturnsViewsNotCopies()
    {
        // 契约是零拷贝视图：源缓冲在发送完成前不该被复用，改源内容视图应立刻可见。
        // 若这里失败，说明实现偷偷拷贝了，发送大数据时会多付出等量的内存与拷贝开销。
        var source = new byte[10];
        var chunks = BleGattFraming.Slice(source, 4);

        source[0] = 0xAA;

        Assert.Equal(0xAA, chunks[0].Span[0]);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(40, 20, 2)]
    public void ChunkCount_EdgesAreOffByOneFree(int length, int payloadSize, int expected)
    {
        Assert.Equal(expected, BleGattFraming.ChunkCount(length, payloadSize));
    }

    [Fact]
    public void Slice_ZeroPayloadSize_Throws()
    {
        // 传 0 进来是调用方 bug（多半是 MTU 换算写错），要立刻炸而不是静默返回空列表。
        Assert.Throws<ArgumentOutOfRangeException>(() => BleGattFraming.Slice(new byte[8], 0));
    }
}
