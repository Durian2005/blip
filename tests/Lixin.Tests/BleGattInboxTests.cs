using Lixin.Transport;

namespace Lixin.Tests;

public class BleGattInboxTests
{
    [Fact]
    public async Task PostThenReceive_ReturnsSameBytes()
    {
        var inbox = new BleGattInbox(capacity: 8);
        var payload = "离线也能发消息"u8.ToArray();

        Assert.True(inbox.TryPost(payload));

        var buffer = new byte[payload.Length];
        var read = await inbox.ReceiveAsync(buffer);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
        Assert.Equal(payload.Length, inbox.PostedBytes);
    }

    [Fact]
    public async Task Post_CopiesData_SoCallerCanReuseBuffer()
    {
        // 这是回调线程场景的核心契约：GATT 回调里拿到的 IBuffer 只在回调期间有效，
        // 回调返回后内容即失效/被复用。TryPost 必须先拷贝，否则上层会读到脏数据。
        var inbox = new BleGattInbox(capacity: 4);
        var source = new byte[4];

        for (var i = 0; i < 3; i++)
        {
            source.AsSpan().Fill((byte)i);
            Assert.True(inbox.TryPost(source));
        }

        source.AsSpan().Fill(0xFF);   // 模拟回调返回后缓冲被协议栈复用

        var buffer = new byte[12];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await inbox.ReceiveAsync(buffer.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2 }, buffer);
    }

    [Fact]
    public async Task Receive_SmallerBuffer_SpansMultipleCalls()
    {
        // 上层给多大缓冲就吐多少，不能假设一次读等于一个块（字节流语义）。
        var inbox = new BleGattInbox(capacity: 4);
        var payload = "0123456789"u8.ToArray();
        Assert.True(inbox.TryPost(payload));

        var buffer = new byte[3];
        var read = await inbox.ReceiveAsync(buffer);
        Assert.Equal(3, read);
        Assert.Equal("012"u8.ToArray(), buffer);

        read = await inbox.ReceiveAsync(buffer);
        Assert.Equal(3, read);
        Assert.Equal("345"u8.ToArray(), buffer);
    }

    [Fact]
    public void TryPost_WhenFull_ReturnsFalseAndCountsOverflow()
    {
        var inbox = new BleGattInbox(capacity: 2);

        Assert.True(inbox.TryPost(new byte[4]));
        Assert.True(inbox.TryPost(new byte[4]));
        Assert.False(inbox.TryPost(new byte[4]));   // 溢出：调用方应立即断链

        Assert.Equal(1, inbox.OverflowCount);
        Assert.True(inbox.IsOverflowed);
    }

    [Fact]
    public async Task Overflow_DoesNotDiscardAlreadyQueuedChunks()
    {
        // 溢出的语义是「新数据进不来」，不是「把旧的清掉」——
        // 断链前仍应把已经收下的字节交出去，否则上层会看到消息被裁掉一半。
        var inbox = new BleGattInbox(capacity: 2);
        Assert.True(inbox.TryPost("AAAA"u8));
        Assert.True(inbox.TryPost("BBBB"u8));
        Assert.False(inbox.TryPost("CCCC"u8));

        var buffer = new byte[8];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await inbox.ReceiveAsync(buffer.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal("AAAABBBB"u8.ToArray(), buffer);
        Assert.Equal(8, inbox.PostedBytes);   // 被拒的那块不计入
    }

    [Fact]
    public async Task Complete_MakesReceiveReturnZero()
    {
        var inbox = new BleGattInbox(capacity: 4);
        inbox.Complete();

        var read = await inbox.ReceiveAsync(new byte[8]);

        // 0 = 对端已关闭，与 IByteChannel 的契约一致（不是抛异常，也不是永久挂起）
        Assert.Equal(0, read);
    }

    [Fact]
    public void Complete_RejectsFurtherPosts_WithoutCountingOverflow()
    {
        // 断链流程自己触发的投递不能污染诊断数据，否则一次正常关闭会被记成一次溢出。
        var inbox = new BleGattInbox(capacity: 4);
        inbox.Complete();

        Assert.False(inbox.TryPost(new byte[4]));
        Assert.Equal(0, inbox.OverflowCount);
        Assert.True(inbox.IsCompleted);
    }

    [Fact]
    public async Task Complete_StillDrainsQueuedData()
    {
        // 关闭时已入队的数据不能被丢：断链时应先把收下的字节读完，再收尾。
        var inbox = new BleGattInbox(capacity: 4);
        Assert.True(inbox.TryPost("hello"u8));
        inbox.Complete();

        var buffer = new byte[5];
        var read = await inbox.ReceiveAsync(buffer);
        Assert.Equal(5, read);
        Assert.Equal("hello"u8.ToArray(), buffer);

        Assert.Equal(0, await inbox.ReceiveAsync(buffer));
    }

    [Fact]
    public async Task Receive_EmptyBuffer_Throws()
    {
        var inbox = new BleGattInbox();
        await Assert.ThrowsAsync<ArgumentException>(async () => await inbox.ReceiveAsync(Memory<byte>.Empty));
    }

    [Fact]
    public void Post_EmptyChunk_IsNotOverflow()
    {
        var inbox = new BleGattInbox(capacity: 1);

        Assert.True(inbox.TryPost(ReadOnlySpan<byte>.Empty));
        Assert.False(inbox.IsOverflowed);
        Assert.Equal(0, inbox.PostedBytes);
    }

    [Fact]
    public void TryPost_ConcurrentWriters_LoseNothing()
    {
        // 回调可能从不同线程进来（GattWriteRequested / ValueChanged 各一条），
        // TryWrite 本身线程安全，但计数与拷贝不得丢数据。
        var inbox = new BleGattInbox(capacity: 2000);

        Parallel.For(0, 1000, _ => Assert.True(inbox.TryPost(new byte[4])));

        Assert.Equal(4000, inbox.PostedBytes);
        Assert.Equal(0, inbox.OverflowCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidCapacity_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BleGattInbox(capacity));
    }
}
