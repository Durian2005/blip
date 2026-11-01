namespace Lixin.Transport;

/// <summary>
/// BLE 通道层的切块与 MTU 换算。
///
/// 这一层的唯一职责：把任意长度的字节塞进 ATT PDU。
/// **它不加任何帧头、不做消息边界** —— 那些是帧层（<c>FrameCodec</c> / <c>FrameReader</c>）的事。
/// 两层分片的分工见 <c>docs/design-t3.1-gatt-channel.md</c> §2：
/// <list type="bullet">
/// <item>帧层：512 B payload + 12 B 帧头，为了消息重组；</item>
/// <item>通道层：MTU − 3，无帧头，为了塞进 PDU。</item>
/// </list>
/// 因为 <c>IByteChannel</c> 是字节流语义，通道层的切块对上层完全透明。
/// </summary>
public static class BleGattFraming
{
    /// <summary>
    /// ATT PDU 的固定开销：opcode(1) + attribute handle(2)。
    /// Write Request 与 Handle Value Notification 都是这个结构，所以两侧扣同样的 3 字节。
    /// </summary>
    public const int AttOverhead = 3;

    /// <summary>BLE 规范规定的 ATT_MTU 默认值（未经协商时的值）。</summary>
    public const int DefaultMtu = 23;

    /// <summary>默认 MTU 下的有效载荷：23 − 3 = 20 B。任何实现的绝对下限。</summary>
    public const int MinPayload = DefaultMtu - AttOverhead;

    /// <summary>
    /// 单次写入的有效载荷上限，与 PRD §7.4 一致。
    /// 实际 MTU 最大也就 517（有效载荷 514），这里的 512 是防御性封顶，
    /// 防止拿到异常大的协商值时一次写出超长 PDU。
    /// </summary>
    public const int MaxPayload = 512;

    /// <summary>
    /// 由 MTU 换算单次可写入的字节数。
    /// </summary>
    /// <param name="mtu">
    /// 协商到的 ATT_MTU（来自 <c>GattSession.MaxPduSize</c>）。
    /// 传 0 或负数表示「还没取到值」，此时按规范默认值 23 兜底 ——
    /// WinRT 没有「请求指定 MTU」的 API，只能观测，所以拿不到值必须有安全默认。
    /// </param>
    /// <remarks>
    /// 返回值被夹在 [20, 512]。
    /// 注意 <paramref name="mtu"/> 小于 23 在规范里不可能出现，若真遇到（协议栈异常），
    /// 仍返回 20 而不是返回一个更小的数：20 是「保证正确」的保守值，
    /// 而返回 <c>mtu − 3</c> 会让写入永远失败，反而更难排查。
    /// </remarks>
    public static int PayloadSizeFor(int mtu)
    {
        if (mtu <= 0)
        {
            mtu = DefaultMtu;
        }

        return Math.Clamp(mtu - AttOverhead, MinPayload, MaxPayload);
    }

    /// <summary>
    /// 按 <paramref name="payloadSize"/> 把数据切成连续片段。
    /// </summary>
    /// <remarks>
    /// 返回的是原内存的**视图，不拷贝**。调用方必须保证在把这些片段全部发送完之前，
    /// 源缓冲不被复用或释放 —— 这与 <c>SendAsync</c> 的常规约定一致（方法返回后即可复用）。
    /// 空输入返回空列表，而不是「一个长度 0 的片段」：后者会让调用方白发一次空写。
    /// </remarks>
    public static List<ReadOnlyMemory<byte>> Slice(ReadOnlyMemory<byte> data, int payloadSize)
    {
        if (payloadSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadSize), payloadSize, "片段大小必须为正数。");
        }

        if (data.IsEmpty)
        {
            return new List<ReadOnlyMemory<byte>>(0);
        }

        var count = ChunkCount(data.Length, payloadSize);
        var chunks = new List<ReadOnlyMemory<byte>>(count);

        for (var offset = 0; offset < data.Length; offset += payloadSize)
        {
            chunks.Add(data.Slice(offset, Math.Min(payloadSize, data.Length - offset)));
        }

        return chunks;
    }

    /// <summary>
    /// 把 <paramref name="length"/> 字节按 <paramref name="payloadSize"/> 切需要几次写入。
    /// 日志和进度估算用；整除以时不额外产生空块。
    /// </summary>
    public static int ChunkCount(int length, int payloadSize)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (payloadSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadSize), payloadSize, "片段大小必须为正数。");
        }

        return (length + payloadSize - 1) / payloadSize;
    }
}
