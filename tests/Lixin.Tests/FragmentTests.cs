using System.Text;
using Lixin.Core.Protocol;

namespace Lixin.Tests;

public class FragmentTests
{
    [Fact]
    public void Fragment_32K_SplitsInto64Frames_AndReassembles()
    {
        var message = new byte[32 * 1024];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)(i * 31 % 256);

        var frames = MessageFragmenter.Fragment(FrameType.Text, 100, message);

        Assert.Equal(64, frames.Count);                  // 32768 / 512
        Assert.Equal(100u, frames[0].Sequence);
        Assert.Equal(163u, frames[^1].Sequence);         // 100 + 63
        Assert.False(frames[0].IsLastFragment);
        Assert.True(frames[^1].IsLastFragment);

        var assembler = new MessageAssembler();
        ReadOnlyMemory<byte> result = default;
        foreach (var frame in frames)
        {
            var status = assembler.Add(frame, out var payload);
            if (status == AssemblyResult.Completed) result = payload;
        }

        Assert.Equal(message, result.ToArray());
        Assert.Equal(0, assembler.CurrentSize);
    }

    [Fact]
    public void Fragment_EmptyPayload_ProducesSingleLastFrame()
    {
        var frames = MessageFragmenter.Fragment(FrameType.Text, 1, ReadOnlyMemory<byte>.Empty);
        Assert.Single(frames);
        Assert.True(frames[0].IsLastFragment);
        Assert.Equal(0, frames[0].Payload.Length);
    }

    [Fact]
    public void Fragment_OverMessageLimit_Throws()
    {
        var tooBig = new byte[ProtocolConstants.MaxMessageSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageFragmenter.Fragment(FrameType.Text, 0, tooBig));
    }

    [Fact]
    public void Assembler_DuplicateFrame_IsIgnored()
    {
        // 12 字节 / 每片 4 字节 = 3 片
        var frames = MessageFragmenter.Fragment(FrameType.Text, 10, Encoding.UTF8.GetBytes("abcdefghijkl"), maxPayload: 4);
        Assert.Equal(3, frames.Count);

        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _)); // 重复投递，忽略
        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[1], out _));
        Assert.Equal(AssemblyResult.Completed, assembler.Add(frames[2], out var payload));
        Assert.Equal("abcdefghijkl", Encoding.UTF8.GetString(payload.Span));
    }

    [Fact]
    public void Assembler_SequenceGap_ReportsErrorAndResets()
    {
        // 12 字节 / 每片 4 字节 = 3 片，才有「跳过第二片」的余地
        var frames = MessageFragmenter.Fragment(FrameType.Text, 10, Encoding.UTF8.GetBytes("abcdefghijkl"), maxPayload: 4);
        Assert.Equal(3, frames.Count);

        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyResult.NeedMore, assembler.Add(frames[0], out _));

        // 跳过第二片，直接投第三片
        Assert.Equal(AssemblyResult.ProtocolError, assembler.Add(frames[2], out _));
        Assert.Equal(0, assembler.CurrentSize);

        // 作废后仍能正常接新消息
        var fresh = MessageFragmenter.Fragment(FrameType.Text, 500, Encoding.UTF8.GetBytes("ok"), maxPayload: 4);
        Assert.Equal(AssemblyResult.Completed, assembler.Add(fresh[0], out var payload));
        Assert.Equal("ok", Encoding.UTF8.GetString(payload.Span));
    }

    [Fact]
    public void Fragment_ThenCodec_ThenReader_EndToEnd()
    {
        var text = string.Concat(Enumerable.Repeat("蓝牙离线通信 blip;", 400)); // 约 6.4 KB
        var frames = MessageFragmenter.Fragment(FrameType.Text, 1, Encoding.UTF8.GetBytes(text));

        var reader = new FrameReader();
        var assembler = new MessageAssembler();
        ReadOnlyMemory<byte> result = default;

        foreach (var frame in frames)
        {
            var bytes = FrameCodec.Encode(frame);
            // 故意按 7 字节一块喂，模拟任意切分
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                reader.Append(bytes.AsSpan(offset, Math.Min(7, bytes.Length - offset)));
                while (reader.TryRead(out var parsed))
                {
                    if (assembler.Add(parsed, out var payload) == AssemblyResult.Completed)
                    {
                        result = payload;
                    }
                }
            }
        }

        Assert.Equal(text, Encoding.UTF8.GetString(result.Span));
    }
}
