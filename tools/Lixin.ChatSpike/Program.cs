using System.Text;
using Lixin.Core.Protocol;
using Lixin.Core.Transport;
using Lixin.Transport;

namespace Lixin.ChatSpike;

/// <summary>
/// P2 端到端验证：两条内存回环通道互发文本，走完整的分片 / 编码 / 解帧 / 重组链路。
/// 没有蓝牙、没有界面，纯命令行。
///
/// 与早期版本的区别：**这个程序会真的断言**。
/// 收错了、少收了、多收了都会打印 FAIL 并以退出码 1 结束，
/// 而不是像以前那样无论结果如何都打印「完成，无丢帧」。
/// 另外它会故意在一条长消息的正文中间插入保活 Ping，用来覆盖「控制帧劫持正文」这个缺陷。
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略不支持的终端 */ }

        Console.WriteLine("=== blip · P2 端到端自聊（内存回环） ===\n");

        // maxChunk 故意设得很小（7 字节），制造严重的「半包」场景：连 12 字节的帧头都过不去
        var (a, b) = LoopbackChannel.CreatePair(maxChunk: 7, seed: 20261026);
        Console.WriteLine($"通道已建立：{a.Name} <-> {b.Name}，单次最大投递 7 字节\n");

        var pumpA = PumpAsync(a, "A");
        var pumpB = PumpAsync(b, "B");

        var textSequence = new SequenceCounter(1);
        var pingSequence = new SequenceCounter(1);   // 控制帧用独立序号空间
        var expected = new[]
        {
            "第一条消息：断网也能聊",
            new string('蓝', 400),                                        // 1200 B → 多帧
            string.Concat(Enumerable.Repeat("长文本压力测试；", 900)),      // 约 7.2 KB → 43 帧
        };

        for (var index = 0; index < expected.Length; index++)
        {
            var bytes = Encoding.UTF8.GetBytes(expected[index]);
            var frames = MessageFragmenter.Fragment(FrameType.Text, textSequence.Current, bytes);
            textSequence.Advance(frames.Count);

            Console.WriteLine($"[A] 发送 Text {bytes.Length} B，切成 {frames.Count} 帧：{Preview(expected[index])}");

            for (var f = 0; f < frames.Count; f++)
            {
                await a.SendAsync(FrameCodec.Encode(frames[f]));

                // 在最长那条消息的第 10 片之后插一条保活 Ping：
                // 旧实现会把正文截成两半各报一次「成功」，这里能直接抓出来
                if (index == 2 && f == 9)
                {
                    var ping = Frame.Create(FrameType.Ping, pingSequence.Current, Encoding.UTF8.GetBytes("PING"));
                    pingSequence.Advance(1);
                    await a.SendAsync(FrameCodec.Encode(ping));
                    Console.WriteLine("[A]   ↳ 在正文中间插入一条 Ping（模拟 P3 的保活）");
                }
            }
        }

        await Task.Delay(800);
        await a.CloseAsync();
        await b.CloseAsync();
        await Task.WhenAll(pumpA, pumpB);

        return Verify(await pumpB, expected, await pumpA);
    }

    private static int Verify(PumpResult receiver, string[] expected, PumpResult sender)
    {
        Console.WriteLine("\n================ 校验 ================");

        var ok = true;

        Console.WriteLine($"收到文本 {receiver.Texts.Count}/{expected.Length} 条，回执 {sender.Acks.Count} 条，保活 {receiver.Pings} 条");

        for (var i = 0; i < expected.Length; i++)
        {
            var match = i < receiver.Texts.Count && receiver.Texts[i] == expected[i];
            Console.WriteLine($"  第 {i + 1} 条（{Encoding.UTF8.GetByteCount(expected[i])} B）：{(match ? "一致" : "**不一致**")}");
            ok &= match;
        }

        if (receiver.Texts.Count != expected.Length)
        {
            Console.WriteLine($"  **条数不符**：期望 {expected.Length}，实际 {receiver.Texts.Count}");
            ok = false;
        }

        if (sender.Acks.Count != expected.Length)
        {
            Console.WriteLine($"  **回执数不符**：期望 {expected.Length}，实际 {sender.Acks.Count}");
            ok = false;
        }

        if (receiver.Pings != 1)
        {
            Console.WriteLine($"  **保活帧数不符**：期望 1，实际 {receiver.Pings}");
            ok = false;
        }

        // 任何一致性错误都意味着重组过程出过问题，即便最终文本侥幸正确也要报出来
        if (receiver.ProtocolErrors + sender.ProtocolErrors > 0)
        {
            Console.WriteLine($"  **协议错误**：接收侧 {receiver.ProtocolErrors} 次，发送侧 {sender.ProtocolErrors} 次");
            ok = false;
        }

        if (receiver.Discarded + sender.Discarded > 0)
        {
            Console.WriteLine($"  **被丢弃帧**：接收侧 {receiver.Discarded}，发送侧 {sender.Discarded}");
            ok = false;
        }

        Console.WriteLine(ok ? "\n结果：PASS —— 全部消息完整、顺序一致、无重复、无丢弃" : "\n结果：FAIL —— 见上方标注");
        Console.WriteLine("=====================================");
        return ok ? 0 : 1;
    }

    private static async Task<PumpResult> PumpAsync(IByteChannel channel, string name)
    {
        var result = new PumpResult(name);
        var reader = new FrameReader();
        var assembler = new MessageAssembler();
        var ackSequence = new SequenceCounter(1000);   // 控制帧，独立序号空间
        var buffer = new byte[16];                     // 读缓冲也故意小于帧头，制造跨包

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
                var status = assembler.Add(frame, out var payload);

                if (status == AssemblyResult.ProtocolError) { result.ProtocolErrors++; continue; }
                if (status == AssemblyResult.Discarded) { result.Discarded++; continue; }
                if (status != AssemblyResult.Completed) continue;

                // 注意：用「完成这一条消息的那一帧」的类型分发
                switch (frame.Type)
                {
                    case FrameType.Text:
                        var text = Encoding.UTF8.GetString(payload.Span);
                        result.Texts.Add(text);
                        Console.WriteLine($"[{name}] 收到 Text {payload.Length} B：{Preview(text)}");

                        var ackFrames = MessageFragmenter.Fragment(
                            FrameType.Ack, ackSequence.Current, Encoding.UTF8.GetBytes($"ACK:{payload.Length}"));
                        ackSequence.Advance(ackFrames.Count);
                        foreach (var ackFrame in ackFrames)
                        {
                            await channel.SendAsync(FrameCodec.Encode(ackFrame));
                        }
                        break;

                    case FrameType.Ack:
                        var ack = Encoding.UTF8.GetString(payload.Span);
                        result.Acks.Add(ack);
                        Console.WriteLine($"[{name}] 收到回执 {ack}");
                        break;

                    case FrameType.Ping:
                        result.Pings++;
                        Console.WriteLine($"[{name}] 收到保活 {Encoding.UTF8.GetString(payload.Span)}（未干扰正文）");
                        break;

                    default:
                        Console.WriteLine($"[{name}] 收到未处理类型 {frame.Type}");
                        break;
                }
            }
        }

        Console.WriteLine($"[{name}] 通道已关闭（残留 {reader.BufferedBytes} B，丢弃 {reader.DiscardedBytes} B）");
        result.TrailingBytes = reader.BufferedBytes;
        result.DiscardedBytes = reader.DiscardedBytes;
        return result;
    }

    private static string Preview(string text)
        => text.Length <= 40 ? text : $"「{text[..20]}…」（共 {text.Length} 字符）";

    private sealed class PumpResult(string name)
    {
        public string Name { get; } = name;
        public List<string> Texts { get; } = new();
        public List<string> Acks { get; } = new();
        public int Pings { get; set; }
        public int ProtocolErrors { get; set; }
        public int Discarded { get; set; }
        public int TrailingBytes { get; set; }
        public long DiscardedBytes { get; set; }
    }

    /// <summary>序号分配器：取当前号，发完按下发帧数推进。</summary>
    private sealed class SequenceCounter(uint start)
    {
        private uint _value = start;

        public uint Current => _value;

        public void Advance(int framesCount) => _value += (uint)framesCount;
    }
}
