using System.Text;
using Lixin.Core.Protocol;
using Lixin.Core.Transport;
using Lixin.Transport;

namespace Lixin.Tests;

public class LoopbackChannelTests
{
    [Fact]
    public async Task Send_ByteByByte_ArrivesIntact()
    {
        var (a, b) = LoopbackChannel.CreatePair(maxChunk: 1, seed: 42);
        var payload = Encoding.UTF8.GetBytes("逐字节到达也要能还原");

        await a.SendAsync(payload);

        var buffer = new byte[payload.Length];
        var total = 0;
        while (total < payload.Length)
        {
            var read = await b.ReceiveAsync(buffer.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(payload, buffer);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Close_MakesPeerReceiveZero()
    {
        var (a, b) = LoopbackChannel.CreatePair(maxChunk: 8, seed: 1);

        await a.CloseAsync();
        Assert.False(a.IsOpen);

        var read = await b.ReceiveAsync(new byte[16]);
        Assert.Equal(0, read);

        await b.DisposeAsync();
    }

    [Fact]
    public async Task Send_AfterClose_Throws()
    {
        var (a, b) = LoopbackChannel.CreatePair();
        await a.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await a.SendAsync(new byte[1]));
        await b.DisposeAsync();
    }

    [Fact]
    public async Task FullStack_MessageSurvivesFragmentationAndRandomChunking()
    {
        var (a, b) = LoopbackChannel.CreatePair(maxChunk: 3, seed: 20261026);

        var text = string.Concat(Enumerable.Repeat("离线也能发消息。", 500)); // 约 4 KB，多帧
        var frames = MessageFragmenter.Fragment(FrameType.Text, 1, Encoding.UTF8.GetBytes(text));

        var sendTask = Task.Run(async () =>
        {
            foreach (var frame in frames)
            {
                await a.SendAsync(FrameCodec.Encode(frame));
            }
            await a.CloseAsync();
        });

        var reader = new FrameReader();
        var assembler = new MessageAssembler();
        string? received = null;
        var buffer = new byte[64];

        while (true)
        {
            var read = await b.ReceiveAsync(buffer);
            if (read == 0) break;
            reader.Append(buffer.AsSpan(0, read));
            while (reader.TryRead(out var frame))
            {
                if (assembler.Add(frame, out var payload) == AssemblyResult.Completed)
                {
                    received = Encoding.UTF8.GetString(payload.Span);
                }
            }
        }

        await sendTask;
        Assert.Equal(text, received);
        await b.DisposeAsync();
    }
}
