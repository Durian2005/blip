using System.Threading.Channels;

namespace Lixin.Transport;

/// <summary>
/// BLE 接收侧的有界缓冲区：把「系统回调推数据」桥接成「上层拉数据」。
///
/// 为什么需要它，以及为什么是这个形状（<c>docs/design-t3.1-gatt-channel.md</c> §4）：
/// <c>GattWriteRequested</c>（外设侧）和 <c>ValueChanged</c>（中心侧）都跑在**系统线程**上，
/// 在那里 <c>await</c> 任何可能长时间阻塞的操作，都会让整个 GATT server/client 停摆 ——
/// 现象是「连接还在但完全不收数据」，而且不报错。所以回调里只允许做三件事：
/// 拷字节、<see cref="TryPost"/>、Respond，全部同步完成。
///
/// 入队失败（= 缓冲溢出）时只有两个选择：丢字节，或者断链。
/// 这里选择**让调用方断链**：丢字节会让帧层在流中间少一段，解析出错位帧，
/// 现象是「偶尔消息内容损坏」—— 这是最难定位的一类故障。宁可断线，不要错位。
/// </summary>
public sealed class BleGattInbox
{
    /// <summary>
    /// 默认容量，单位是「块」。按最大 244 B/块算约 62 KB，
    /// 足以吸收单条 32 KB 消息（64 帧 × 约 512 B）的突发，内存占用可忽略。
    /// </summary>
    public const int DefaultCapacity = 256;

    private readonly Channel<byte[]> _inbox;

    private byte[]? _current;
    private int _currentOffset;
    private long _postedBytes;
    private int _overflowCount;
    private volatile bool _completed;

    /// <param name="capacity">队列块数上限。生产代码用默认值，测试才传小值。</param>
    public BleGattInbox(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量必须为正数。");
        }

        Capacity = capacity;

        _inbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            // Wait 模式下 TryWrite 在满时直接返回 false —— 这正是「溢出要能被发现」的语义。
            // 不能用 DropOldest/DropWrite：那会静默丢数据，正是本类要避免的事。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>队列块数上限。</summary>
    public int Capacity { get; }

    /// <summary>累计接收的字节数，用于诊断与吞吐估算。</summary>
    public long PostedBytes => Interlocked.Read(ref _postedBytes);

    /// <summary>累计溢出次数。任何非零值都意味着这条链路应当被断开重建。</summary>
    public int OverflowCount => Volatile.Read(ref _overflowCount);

    /// <summary>是否发生过溢出。</summary>
    public bool IsOverflowed => OverflowCount > 0;

    /// <summary>是否已关闭（关闭后不再接受数据）。</summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// 在系统回调线程里同步投递一块数据。
    /// </summary>
    /// <returns>
    /// true = 已入队；false = 队列已满或已关闭，调用方应**立即断链**，
    /// 不要重试、不要跳过这一块继续收 —— 跳过等于在字节流中间挖洞。
    /// </returns>
    /// <remarks>
    /// 无数据时返回 true（不算溢出）。入队前先拷贝是必须的：
    /// GATT 回调里拿到的 <c>IBuffer</c> 只在回调期间有效，回调返回后内容即失效。
    /// 溢出时这次拷贝会白做，但溢出是异常路径且马上断链，不值得为它优化。
    /// </remarks>
    public bool TryPost(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return true;
        }

        if (_completed)
        {
            return false;
        }

        var copy = chunk.ToArray();

        if (!_inbox.Writer.TryWrite(copy))
        {
            // 关闭后投递不算溢出，只有「真满了」才计数：
            // 否则断链流程自己触发的投递会把诊断数据污染成一次假溢出。
            Interlocked.Increment(ref _overflowCount);
            return false;
        }

        Interlocked.Add(ref _postedBytes, copy.Length);
        return true;
    }

    /// <summary>
    /// 读出字节，语义与 <c>IByteChannel.ReceiveAsync</c> 完全一致：
    /// <b>返回 0 表示已关闭，不会再有任何数据</b>。
    /// </summary>
    /// <remarks>同一时刻只允许一个读者（与 <c>Stream</c> 一致）。</remarks>
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 契约里 0 专门表示「已关闭」，所以空缓冲必须报错而不是返回 0，
        // 否则上层会把「调用方传了个空数组」误判成断线。
        if (buffer.IsEmpty)
        {
            throw new ArgumentException("读缓冲不能为空：返回 0 在本接口中表示通道已关闭。", nameof(buffer));
        }

        while (true)
        {
            if (_current is not null && _currentOffset < _current.Length)
            {
                var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
                _current.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
                _currentOffset += count;
                return count;
            }

            if (!await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_inbox.Reader.TryRead(out var chunk))
            {
                _current = chunk;
                _currentOffset = 0;
            }
        }
    }

    /// <summary>
    /// 关闭接收侧：让后续 <see cref="ReceiveAsync"/> 读到 0，并拒绝新的投递。
    /// 已入队但未读走的数据**不会**被丢弃，仍会被读出来 —— 断链时应先把已有数据读完再收尾。
    /// </summary>
    public void Complete()
    {
        _completed = true;
        _inbox.Writer.TryComplete();
    }
}
