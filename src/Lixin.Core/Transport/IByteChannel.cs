namespace Lixin.Core.Transport;

/// <summary>
/// 字节通道抽象——整个项目唯一的平台相关边界。
/// BLE / RFCOMM / 内存回环都实现它，上层（帧层、安全层、界面）对传输一无所知。
/// 语义对齐 socket：<see cref="ReceiveAsync"/> 返回 0 表示对端已关闭。
/// </summary>
public interface IByteChannel : IAsyncDisposable
{
    /// <summary>通道名称，仅用于日志。</summary>
    string Name { get; }

    /// <summary>是否仍然可用（可发送）。</summary>
    bool IsOpen { get; }

    /// <summary>
    /// 读取数据到 <paramref name="buffer"/>，返回实际读到的字节数。
    /// 返回 0 表示对端已关闭，不会再有任何数据。
    /// </summary>
    /// <remarks>
    /// **同一时刻只允许一个读者**（与 <see cref="System.IO.Stream"/> 一致）。
    /// <paramref name="buffer"/> 为空抛 <see cref="ArgumentException"/>——0 在本接口中专指对端关闭，
    /// 不能与「没读到东西」混用。
    /// </remarks>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>发送一段数据。允许并发调用，顺序由实现保证。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>关闭本端。对端随后会读到 0。</summary>
    Task CloseAsync();
}
