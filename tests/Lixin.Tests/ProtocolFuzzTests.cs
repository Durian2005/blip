using System.Text;
using Lixin.Core.Protocol;

namespace Lixin.Tests;

/// <summary>
/// 帧层的模糊测试与性质测试（2026-10-31 补）。
///
/// 手写用例只能覆盖"人想到的边界"，这两类测试覆盖"人没想到的输入"：
/// <list type="bullet">
/// <item>模糊测试：随机字节流 / 随机损坏 / 随机帧序列，只断言**不变量**——
///       不崩、缓冲有界、重组器不超限、结果值不逃逸契约、干净信道必然恢复；</item>
/// <item>性质测试：任意长度 × 任意分片粒度 × 任意粘包切点 × 随机位置插控制帧，
///       重组结果必须逐一字节等于原文（200 组随机种子，与 T3.2 选举测试同款模式）。</item>
/// </list>
/// 全部用固定种子，失败可复现：种子即用例。
/// </summary>
public class ProtocolFuzzTests
{
    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>把 reader 当前能吐的帧全部吐出来，过一遍组装器，返回是否收到过指定的 Ping。</summary>
    private static bool Drain(FrameReader reader, MessageAssembler assembler)
    {
        var sawPing = false;
        while (reader.TryRead(out var frame))
        {
            var result = assembler.Add(frame, out _);
            if (result == AssemblyResult.Completed && frame.Type == FrameType.Ping)
            {
                sawPing = true;
            }
        }

        return sawPing;
    }

    // =====================================================================
    // C1-a：纯随机字节流。 reader 的核心承诺：给什么都别崩，缓冲别爆炸。
    // =====================================================================

    [Fact]
    public void RandomByteStream_NeverCrashes_AndBufferStaysBounded()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var rng = new Random(seed);
            var reader = new FrameReader();
            var garbage = RandomBytes(rng, 4096);

            // 随机大小（1..300 B）喂入，同时覆盖半包、粘包、跨块帧头
            var offset = 0;
            while (offset < garbage.Length)
            {
                var size = rng.Next(1, 301);
                reader.Append(garbage.AsSpan(offset, Math.Min(size, garbage.Length - offset)));
                offset += size;

                while (reader.TryRead(out var frame))
                {
                    AssertEmittedFrameIsSelfConsistent(frame);
                }
            }

            // 能走到这里就是"没崩"。缓冲上界：一个待齐的帧（头 12 + 载荷最多 512）
            // 再加最后一次追加的块（最多 300）。
            Assert.True(reader.BufferedBytes <= ProtocolConstants.HeaderSize + ProtocolConstants.MaxPayload + 300,
                $"seed {seed}：缓冲涨到 {reader.BufferedBytes} 字节，reader 没有在同步。");
        }
    }

    /// <summary>reader 吐出来的帧必须自洽：重新编码再解析，必须得到同一个帧（用 M2 的内容相等性）。</summary>
    private static void AssertEmittedFrameIsSelfConsistent(Frame frame)
    {
        Assert.True(frame.Payload.Length <= ProtocolConstants.MaxPayload,
            $"吐出了 {frame.Payload.Length} 字节载荷的帧，超过单帧上限。");
        Assert.True(frame.AuthTag.Length is ProtocolConstants.NoAuthTag or ProtocolConstants.AuthTagSize,
            $"吐出了 TAGLEN={frame.AuthTag.Length} 的帧，非法值本不该出现在上层。");

        var reEncoded = FrameCodec.Encode(frame);
        Assert.True(FrameCodec.TryParse(reEncoded, out var reparsed, out var consumed));
        Assert.Equal(reEncoded.Length, consumed);
        Assert.Equal(frame, reparsed);
    }

    // =====================================================================
    // C1-b：对合法流做随机损坏。组装器的承诺：任何输入都不逃逸契约，
    //       且干净信道（重连语义）之后必然恢复。
    // =====================================================================

    [Fact]
    public void RandomCorruption_KeepsAssemblerInContract_AndRecoversOnCleanChannel()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var rng = new Random(seed);

            // 一条 600 B 正文（3 片 × 200 B）后跟一条 Ping
            var text = RandomBytes(rng, 600);
            var wire = MessageFragmenter.Fragment(FrameType.Text, 1, text, 200)
                .SelectMany(f => FrameCodec.Encode(f))
                .Concat(FrameCodec.Encode(Frame.Create(FrameType.Ping, 0, "PING"u8.ToArray())))
                .ToArray();

            // 随机翻转 1..7 个字节（可能命中帧头、载荷、魔数）
            for (var i = 0; i < rng.Next(1, 8); i++)
            {
                wire[rng.Next(wire.Length)] = (byte)rng.Next(256);
            }

            var reader = new FrameReader();
            var assembler = new MessageAssembler();

            var offset = 0;
            while (offset < wire.Length)
            {
                var size = rng.Next(1, 998);   // 故意制造半包与跨帧粘包
                reader.Append(wire.AsSpan(offset, Math.Min(size, wire.Length - offset)));
                offset += size;
                Drain(reader, assembler);
            }

            Drain(reader, assembler);

            // 不变量：缓冲有界、组装中的消息不超单条上限
            Assert.True(reader.BufferedBytes <= ProtocolConstants.HeaderSize + ProtocolConstants.MaxPayload + 997,
                $"seed {seed}：损坏流之后缓冲未收敛（{reader.BufferedBytes} B）。");
            Assert.True(assembler.CurrentSize <= ProtocolConstants.MaxMessageSize,
                $"seed {seed}：组装缓冲 {assembler.CurrentSize} B 超过单条消息上限。");

            // 恢复的确定性证明：一条被损坏的流之后，干净信道上的一条完好消息
            // 必须一字不差地交付（这就是断线重连的语义——新连接是干净的信道）。
            var freshReader = new FrameReader();
            var freshAssembler = new MessageAssembler();
            var recovered = MessageFragmenter.Fragment(
                FrameType.Text, 9000, "recovered-after-corruption"u8.ToArray(), 100);

            ReadOnlyMemory<byte> delivered = default;
            foreach (var frame in recovered)
            {
                freshReader.Append(FrameCodec.Encode(frame));
                while (freshReader.TryRead(out var parsed))
                {
                    if (freshAssembler.Add(parsed, out var payload) == AssemblyResult.Completed)
                    {
                        delivered = payload;
                    }
                }
            }

            Assert.Equal("recovered-after-corruption", Encoding.UTF8.GetString(delivered.Span));
        }
    }

    // =====================================================================
    // C1-c：直接往组装器里倒随机帧。承诺：不崩、缓冲不超限、Reset 永远有效。
    // =====================================================================

    [Fact]
    public void RandomFrames_NeverCrashAssembler_OrOversizeBuffer()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var rng = new Random(seed);
            var assembler = new MessageAssembler();

            for (var i = 0; i < 2000; i++)
            {
                // 类型：一半是已知类型，一半是随机字节（未知类型按控制帧处理是既定设计）
                var known = new[] { FrameType.Text, FrameType.Ping, FrameType.Ack, FrameType.Bye };
                var type = rng.Next(2) == 0
                    ? (FrameType)rng.Next(256)
                    : known[rng.Next(known.Length)];

                var frame = Frame.Create(
                    type,
                    (uint)rng.Next(0, 4000),
                    RandomBytes(rng, rng.Next(0, 600)),   // 故意超过 512：帧层才会拦，组装器只见帧
                    lastFragment: rng.Next(2) == 0);

                assembler.Add(frame, out var payload);

                if (payload.Length > ProtocolConstants.MaxMessageSize)
                {
                    Assert.Fail($"seed {seed} 第 {i} 帧：交付了 {payload.Length} 字节，超过单条消息上限。");
                }

                Assert.True(assembler.CurrentSize <= ProtocolConstants.MaxMessageSize,
                    $"seed {seed} 第 {i} 帧：组装缓冲 {assembler.CurrentSize} B 超限。");
            }

            assembler.Reset();
            Assert.Equal(0, assembler.CurrentSize);
            Assert.False(assembler.IsPoisoned);
        }
    }

    // =====================================================================
    // C2：重组性质测试。原文 = 任意长度随机字节；管道 = 任意分片粒度
    //     （1..512）× 任意粘包切点（1..4000）× 随机位置插一条控制帧。
    //     终点必须逐一字节还原原文，且控制帧单独交付、不占数据序号空间。
    // =====================================================================

    [Fact]
    public void AnyLengthAnyChunkingWithInterleavedPing_ReassemblesOriginal()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var rng = new Random(seed);

            var length = rng.Next(0, 20 * 1024);          // 覆盖 0、1、刚好一片、跨多片
            var message = RandomBytes(rng, length);
            var maxPayload = rng.Next(1, ProtocolConstants.MaxPayload + 1);
            var frames = MessageFragmenter.Fragment(FrameType.Text, (uint)rng.Next(0, 1000), message, maxPayload);

            var encoded = frames.Select(f => FrameCodec.Encode(f)).ToList();

            // 在随机**帧边界**插入一条 Ping（控制帧：旁路重组 + 独立序号空间）。
            // 注意只能插在帧与帧之间：真实发送端把 Ping 当作独立的一帧入队，
            // 绝不会把字节撕开。插到帧中间等于破坏字节流，没有 AEAD  TAG 的
            // P2 阶段物理上不可恢复——那种损坏归 C1-b 的随机翻转测试管，
            // 这里只验证"干净流 + 合法交错"必须无损。
            var pingWire = FrameCodec.Encode(Frame.Create(FrameType.Ping, 42, "PING"u8.ToArray()));
            var pingAfterFrame = rng.Next(0, encoded.Count + 1);
            var stream = encoded.Take(pingAfterFrame)
                .Concat(new[] { pingWire })
                .Concat(encoded.Skip(pingAfterFrame))
                .SelectMany(b => b)
                .ToArray();

            var reader = new FrameReader();
            var assembler = new MessageAssembler();
            var results = new HashSet<AssemblyResult>();
            ReadOnlyMemory<byte> deliveredText = default;
            ReadOnlyMemory<byte> deliveredPing = default;

            var offset = 0;
            while (offset < stream.Length)
            {
                var size = rng.Next(1, 4001);
                reader.Append(stream.AsSpan(offset, Math.Min(size, stream.Length - offset)));
                offset += size;

                while (reader.TryRead(out var frame))
                {
                    var result = assembler.Add(frame, out var payload);
                    results.Add(result);
                    if (result == AssemblyResult.Completed)
                    {
                        if (frame.Type == FrameType.Ping)
                        {
                            deliveredPing = payload;
                        }
                        else
                        {
                            deliveredText = payload;
                        }
                    }
                }
            }

            // 干净流上只允许出现 NeedMore 与 Completed：任何 Discarded/ProtocolError
            // 都说明协议实现在"本应无损"的管道上丢了数据
            // （单帧消息时 NeedMore 本就不会出现，所以不断言它一定在）
            Assert.DoesNotContain(AssemblyResult.ProtocolError, results);
            Assert.DoesNotContain(AssemblyResult.Discarded, results);

            Assert.Equal(message, deliveredText.ToArray());
            Assert.Equal("PING", Encoding.UTF8.GetString(deliveredPing.Span));
            Assert.Equal(0, reader.BufferedBytes);
            Assert.Equal(0, assembler.CurrentSize);
        }
    }
}
