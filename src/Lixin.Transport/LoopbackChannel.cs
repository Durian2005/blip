using System.Threading.Channels;
using Lixin.Core.Transport;

namespace Lixin.Transport;

/// <summary>
/// 内存回环通道：一对互相连通的 IByteChannel，用于在没有蓝牙硬件时开发和测试。
///
/// 为什么不用真 TCP：真 TCP 需要监听端口（会触发防火墙提示），而且分片行为不可控；
/// 这里改成「按随机大小切块」投递，能稳定复现粘包 / 半包 / 逐字节到达等真实场景，
/// 单测可重复（传 seed 即可）。
/// </summary>
public sealed class LoopbackChannel : IByteChannel
{
    private readonly Channel<byte[]> _inbox;
    private readonly Channel<byte[]> _peerInbox;
    private readonly int _maxChunk;
    private readonly Random _random;

    private byte[]? _current;
    private int _currentOffset;
    private bool _closed;

    private LoopbackChannel(string name, Channel<byte[]> inbox, Channel<byte[]> peerInbox, int maxChunk, int seed)
    {
        Name = name;
        _inbox = inbox;
        _peerInbox = peerInbox;
        _maxChunk = maxChunk;
        _random = new Random(seed);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsOpen => !_closed;

    /// <summary>
    /// 创建一对互通的通道。
    /// </summary>
    /// <param name="maxChunk">单次投递的最大字节数，1 表示逐字节到达（最坏情况）。</param>
    /// <param name="seed">随机种子，传固定值可让分片行为可复现。</param>
    public static (LoopbackChannel A, LoopbackChannel B) CreatePair(int maxChunk = 64, int? seed = null)
    {
        if (maxChunk <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunk));

        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        var baseSeed = seed ?? Random.Shared.Next();

        var a = new LoopbackChannel("loopback-A", aToB, bToA, maxChunk, baseSeed ^ 0x5F5F);
        var b = new LoopbackChannel("loopback-B", bToA, aToB, maxChunk, baseSeed ^ 0x3A3A);
        return (a, b);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (data.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        var buffer = data.ToArray();
        var offset = 0;

        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var size = Math.Min(_random.Next(1, _maxChunk + 1), buffer.Length - offset);
            var chunk = buffer.AsSpan(offset, size).ToArray();

            if (!_peerInbox.Writer.TryWrite(chunk))
            {
                throw new InvalidOperationException($"{Name}：对端已关闭，无法发送。");
            }

            offset += size;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
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

    /// <inheritdoc />
    public Task CloseAsync()
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        _closed = true;

        // 让对端的 ReceiveAsync 读到 0
        _peerInbox.Writer.TryComplete();
        _inbox.Writer.TryComplete();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
