using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Lixin.Core.Protocol;

/// <summary>
/// 增量帧读取器：喂任意长度的字节（半包、粘包、碎片），吐出完整帧。
/// 具备重新同步能力：遇到垃圾数据或非法长度时向前找魔数，而不是直接崩溃。
/// </summary>
public sealed class FrameReader
{
    private readonly List<byte> _buffer = new();

    /// <summary>当前缓冲区里还没被解析的字节数。</summary>
    public int BufferedBytes => _buffer.Count;

    /// <summary>累计丢掉的无效字节数，用于诊断链路质量。</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>追加数据（可以只有半个帧头）。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        _buffer.AddRange(data.ToArray());
    }

    /// <summary>
    /// 尝试取出一帧。返回 false 表示数据还不够，调用方应继续 Append。
    /// </summary>
    public bool TryRead(out Frame frame)
    {
        frame = default;

        while (true)
        {
            if (_buffer.Count < ProtocolConstants.HeaderSize)
            {
                return false;
            }

            var span = CollectionsMarshal.AsSpan(_buffer);

            // 魔数不对：向前找下一个魔数，找不到就只留最后 1 字节（可能跨包）
            if (BinaryPrimitives.ReadUInt16BigEndian(span) != ProtocolConstants.Magic)
            {
                var next = FindMagic(span, 1);
                if (next < 0)
                {
                    var drop = _buffer.Count - 1;
                    if (drop > 0)
                    {
                        _buffer.RemoveRange(0, drop);
                        DiscardedBytes += drop;
                    }
                    return false;
                }

                _buffer.RemoveRange(0, next);
                DiscardedBytes += next;
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(span[9..]);

            // 长度非法（超过单帧上限）：丢弃当前首字节再同步，避免无限等待
            if (payloadLength > ProtocolConstants.MaxPayload)
            {
                _buffer.RemoveAt(0);
                DiscardedBytes++;
                continue;
            }

            if (!FrameCodec.TryParse(span, out frame, out var consumed))
            {
                return false;
            }

            _buffer.RemoveRange(0, consumed);
            return true;
        }
    }

    private static int FindMagic(ReadOnlySpan<byte> span, int startIndex)
    {
        var hi = (byte)(ProtocolConstants.Magic >> 8);
        var lo = (byte)(ProtocolConstants.Magic & 0xFF);

        for (var i = startIndex; i + 1 < span.Length; i++)
        {
            if (span[i] == hi && span[i + 1] == lo)
            {
                return i;
            }
        }

        return -1;
    }
}
