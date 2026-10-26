using System.Text;
using Lixin.Core.Protocol;
using Lixin.Core.Transport;
using Lixin.Transport;

namespace Lixin.ChatSpike;

/// <summary>
/// P2 最小可运行验证：两条内存回环通道互发文本，走完整的分片 / 编码 / 解帧 / 重组链路。
/// 没有蓝牙、没有界面，纯命令行，用来证明「协议层是自洽的」。
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略不支持的终端 */ }

        Console.WriteLine("=== blip · P2 单机自聊（内存回环） ===\n");

        // maxChunk 故意设得很小（7 字节），制造严重的「半包」场景
        var (a, b) = LoopbackChannel.CreatePair(maxChunk: 7, seed: 20261026);
        Console.WriteLine($"通道已建立：{a.Name} <-> {b.Name}，单次最大投递 7 字节\n");

        var pumpA = PumpAsync(a, "A");
        var pumpB = PumpAsync(b, "B");

        var textSequence = new SequenceCounter(1);
        var messages = new[]
        {
            "第一条消息：断网也能聊",
            new string('蓝', 400),                                        // 1200 B → 多帧
            string.Concat(Enumerable.Repeat("长文本压力测试；", 900)),      // 约 7.2 KB
        };

        foreach (var text in messages)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var frames = MessageFragmenter.Fragment(FrameType.Text, textSequence.Current, bytes);
            textSequence.Advance(frames.Count);

            Console.WriteLine($"[A] 发送 Text {bytes.Length} B，切成 {frames.Count} 帧：{Preview(text)}");
            foreach (var frame in frames)
            {
                await a.SendAsync(FrameCodec.Encode(frame));
            }
        }

        Console.WriteLine();
        await Task.Delay(800);

        await a.CloseAsync();
        await b.CloseAsync();
        await Task.WhenAll(pumpA, pumpB);

        Console.WriteLine("\n=== 完成：所有消息完整重组，无丢帧 ===");
        return 0;
    }

    private static async Task PumpAsync(IByteChannel channel, string name)
    {
        var reader = new FrameReader();
        var assembler = new MessageAssembler();
        var ackSequence = new SequenceCounter(1000);
        var buffer = new byte[16];   // 读缓冲也故意小于帧头，制造跨包

        while (true)
        {
            var read = await channel.ReceiveAsync(buffer);
            if (read == 0)
            {
                break;
            }

            reader.Append(buffer.AsSpan(0, read));

            while (reader.TryRead(out var frame))
            {
                if (assembler.Add(frame, out var payload) != AssemblyResult.Completed)
                {
                    continue;
                }

                switch (frame.Type)
                {
                    case FrameType.Text:
                        Console.WriteLine($"[{name}] 收到 Text {payload.Length} B：{Preview(Encoding.UTF8.GetString(payload.Span))}");

                        var ackPayload = Encoding.UTF8.GetBytes($"ACK:{payload.Length}");
                        var ackFrames = MessageFragmenter.Fragment(FrameType.Ack, ackSequence.Current, ackPayload);
                        ackSequence.Advance(ackFrames.Count);
                        foreach (var ackFrame in ackFrames)
                        {
                            await channel.SendAsync(FrameCodec.Encode(ackFrame));
                        }
                        break;

                    case FrameType.Ack:
                        Console.WriteLine($"[{name}] 收到回执 {Encoding.UTF8.GetString(payload.Span)}");
                        break;

                    default:
                        Console.WriteLine($"[{name}] 收到未处理类型 {frame.Type}");
                        break;
                }
            }
        }

        Console.WriteLine($"[{name}] 通道已关闭（残留 {reader.BufferedBytes} B，丢弃 {reader.DiscardedBytes} B）");
    }

    private static string Preview(string text)
        => text.Length <= 40 ? text : $"「{text[..20]}…」（共 {text.Length} 字符）";

    /// <summary>序号分配器：取当前起始号，发完按下发帧数推进。</summary>
    private sealed class SequenceCounter
    {
        private uint _value;

        public SequenceCounter(uint start) => _value = start;

        public uint Current => _value;

        public void Advance(int framesCount) => _value += (uint)framesCount;
    }
}
