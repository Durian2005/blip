using System.Text;
using Lixin.Core.Protocol;
using Lixin.Transport;

namespace Lixin.Tests;

/// <summary>
/// 分片重组的健壮性回归测试。
/// 每一条都对应 2026-10-26 代码审查中确认过的缺陷（见 docs/code-review-2026-10-26.md 的 C2/C3/H1）。
/// 命名用「现象」而不是「实现」，改实现时这些测试仍然应该成立。
/// </summary>
public class AssemblerRobustnessTests
{
    private static IReadOnlyList<Frame> TextFrames(uint startSeq, string text, int maxPayload)
        => MessageFragmenter.Fragment(FrameType.Text, startSeq, Encoding.UTF8.GetBytes(text), maxPayload);

    /// <summary>C3：丢掉中间一片后，残余分片绝不能被当成一条「完整」消息交付。</summary>
    [Fact]
    public void LostMiddleFragment_ResidualTailIsNotDeliveredAsCompleteMessage()
    {
        // 16 字节 → 4 片（seq 10,11,12,13）
        var frames = TextFrames(10, "abcdefghijklmnop", maxPayload: 4);
        Assert.Equal(4, frames.Count);

        var assembler = new MessageAssembler();
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));

        // 第 2 片丢失，直接收到第 3 片 → 必须报错
        Assert.Equal(AssemblyResult.ProtocolError, assembler.Add(frames[2], out _));

        // 第 4 片属于那条已损坏的消息，必须被丢弃，而不是「新消息的末片」
        var result = assembler.Add(frames[3], out var payload);
        Assert.Equal(AssemblyResult.Discarded, result);
        Assert.True(payload.IsEmpty, "损坏消息的残余内容绝不能作为独立消息交付");
    }

    /// <summary>C2：正文传输途中插入的单帧控制帧不得劫持正文。</summary>
    [Fact]
    public void SingleFrameControlMessage_DoesNotHijackInFlightText()
    {
        var text = new string('A', 300);
        var frames = TextFrames(1, text, maxPayload: 100);   // seq 1,2,3
        Assert.Equal(3, frames.Count);

        var assembler = new MessageAssembler();
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));

        // 保活帧在正文中间到达。控制帧用独立序号空间，与数据帧的 2/3 无关。
        var ping = Frame.Create(FrameType.Ping, 1, Encoding.UTF8.GetBytes("PING"));
        Assert.Equal(AssemblyResult.Completed, assembler.Add(ping, out var pingPayload));
        Assert.Equal("PING", Encoding.UTF8.GetString(pingPayload.Span));

        // 正文必须原样继续：第 2 片、第 3 片仍能连续组装
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[1], out _));
        Assert.Equal(AssemblyResult.Completed, assembler.Add(frames[2], out var textPayload));
        Assert.Equal(text, Encoding.UTF8.GetString(textPayload.Span));
    }

    /// <summary>控制帧不占用数据序号空间，因此不能导致正文被判为断裂。</summary>
    [Fact]
    public void ControlFrames_DoNotConsumeDataSequenceSpace()
    {
        var frames = TextFrames(500, "0123456789", maxPayload: 5);  // seq 500,501
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));

        // 连续三条控制帧，序号随便给（控制帧序号不参与校验）
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(AssemblyResult.Completed,
                assembler.Add(Frame.Create(FrameType.Pong, (uint)i, ReadOnlyMemory<byte>.Empty), out _));
        }

        Assert.Equal(AssemblyResult.Completed, assembler.Add(frames[1], out var payload));
        Assert.Equal("0123456789", Encoding.UTF8.GetString(payload.Span));
    }

    /// <summary>控制帧被误标为「非末片」时是畸形输入，应当报错且不得污染正文。</summary>
    [Fact]
    public void FragmentedControlFrame_IsRejectedWithoutPoisoningText()
    {
        var frames = TextFrames(1, "abcdef", maxPayload: 3);   // seq 1,2
        var assembler = new MessageAssembler();
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));

        var malformedPing = Frame.Create(FrameType.Ping, 9, Encoding.UTF8.GetBytes("x"), lastFragment: false);
        Assert.Equal(AssemblyResult.ProtocolError, assembler.Add(malformedPing, out _));

        // 正文不应因此被毒化，仍能正常收完
        Assert.Equal(AssemblyResult.Completed, assembler.Add(frames[1], out var payload));
        Assert.Equal("abcdef", Encoding.UTF8.GetString(payload.Span));
    }

    /// <summary>毒化期不能永久失聪：下一条正常消息必须能让流恢复。</summary>
    [Fact]
    public void AfterProtocolError_NextCompleteMessageIsAccepted()
    {
        var broken = TextFrames(10, "abcdefgh", maxPayload: 4);   // seq 10,11
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(broken[0], out _));

        // 一条序号跳得很远的「非末片」制造断裂 → 进入毒化
        Assert.Equal(AssemblyResult.ProtocolError,
            assembler.Add(Frame.Create(FrameType.Text, 900, Encoding.UTF8.GetBytes("junk"), lastFragment: false), out _));
        Assert.True(assembler.IsPoisoned);

        // 毒化期间的非末片被丢弃
        Assert.Equal(AssemblyResult.Discarded,
            assembler.Add(Frame.Create(FrameType.Text, 901, ReadOnlyMemory<byte>.Empty, lastFragment: false), out _));

        // 下一条完整消息（末片）解除毒化，但这一条本身作为「余波」被丢掉
        var swallowed = TextFrames(1000, "hello", maxPayload: 10);
        Assert.Equal(AssemblyResult.Discarded, assembler.Add(swallowed[0], out _));
        Assert.False(assembler.IsPoisoned, "收到末片后应解除毒化");

        var next = TextFrames(1010, "world", maxPayload: 10);
        Assert.Equal(AssemblyResult.Completed, assembler.Add(next[0], out var payload));
        Assert.Equal("world", Encoding.UTF8.GetString(payload.Span));
    }

    /// <summary>末片丢失时毒化不会自行结束，但代价有界：最多再白丢一条消息，随后自动恢复。</summary>
    [Fact]
    public void LostLastFragment_LosesAtMostOneExtraMessage()
    {
        var assembler = new MessageAssembler();
        Assert.Equal(AssemblyResult.NeedMore,
            assembler.Add(Frame.Create(FrameType.Text, 1, Encoding.UTF8.GetBytes("ab"), lastFragment: false), out _));

        // 序号跳到很远 → 毒化
        Assert.Equal(AssemblyResult.ProtocolError,
            assembler.Add(Frame.Create(FrameType.Text, 5000, Encoding.UTF8.GetBytes("cd"), lastFragment: false), out _));

        // 之后一直只有「非末片」到达，模拟末片永久丢失
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(AssemblyResult.Discarded, assembler.Add(
                Frame.Create(FrameType.Text, (uint)(5001 + i), ReadOnlyMemory<byte>.Empty, lastFragment: false), out _));
        }

        Assert.True(assembler.IsPoisoned, "没见过末片就应保持毒化，否则会把残余拼成假消息");

        // 第一条完整消息被当作余波丢弃，同时解除毒化
        Assert.Equal(AssemblyResult.Discarded,
            assembler.Add(Frame.Create(FrameType.Text, 9000, Encoding.UTF8.GetBytes("lost"), lastFragment: true), out _));
        Assert.False(assembler.IsPoisoned);

        // 真正的下一条恢复正常
        var next = TextFrames(9100, "ok", maxPayload: 10);
        Assert.Equal(AssemblyResult.Completed, assembler.Add(next[0], out var payload));
        Assert.Equal("ok", Encoding.UTF8.GetString(payload.Span));
    }

    /// <summary>断线重连时 Reset 必须把毒化状态也一并清掉。</summary>
    [Fact]
    public void Reset_ClearsPoisonedState()
    {
        var assembler = new MessageAssembler();
        assembler.Add(Frame.Create(FrameType.Text, 1, ReadOnlyMemory<byte>.Empty, lastFragment: false), out _);
        assembler.Add(Frame.Create(FrameType.Text, 99, ReadOnlyMemory<byte>.Empty, lastFragment: false), out _);
        Assert.True(assembler.IsPoisoned);

        assembler.Reset();
        Assert.False(assembler.IsPoisoned);
        Assert.Equal(0, assembler.CurrentSize);

        var next = TextFrames(1, "back", maxPayload: 10);
        Assert.Equal(AssemblyResult.Completed, assembler.Add(next[0], out var payload));
        Assert.Equal("back", Encoding.UTF8.GetString(payload.Span));
    }

    /// <summary>H1：空读缓冲是调用方的编码错误，不能伪装成「对端已关闭」。</summary>
    [Fact]
    public async Task EmptyReadBuffer_ThrowsInsteadOfSignallingEof()
    {
        var (a, b) = LoopbackChannel.CreatePair();
        await Assert.ThrowsAsync<ArgumentException>(async () => await b.ReceiveAsync(Memory<byte>.Empty));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }
}
