using Lixin.Core.Connection;
using Lixin.Core.Transport;

namespace Lixin.Transport;

/// <summary>
/// 给 <see cref="ConnectionSupervisor"/> 用的 BLE 连接工厂（design-t3.3 §3）。
///
/// 监督者对角色一无所知，它只拿一个「建立已就绪链路」的委托；这里把两侧的角色语义
/// 装进委托：
/// <list type="bullet">
/// <item><see cref="Central"/>：连接（首连扫描，重连直连已知地址），返回即链路就绪；</item>
/// <item><see cref="Peripheral"/>：启动广播后**等首个订阅者**——广播只是待命，
///   订阅者出现才算链路建立（<see cref="BleGattPeripheralChannel.ClientConnected"/>）。</item>
/// </list>
/// </summary>
public static class BleConnectionFactories
{
    /// <summary>
    /// 中心侧工厂。每次调用读一次地址提供者：
    /// 返回 null = 扫描（首次连接）；返回地址 = 直连（重连，跳过扫描）。
    /// </summary>
    /// <param name="addressProvider">
    /// 提供「最近一次成功连接的对端地址」。上层在 Connected 事件里把
    /// <see cref="BleGattCentralChannel.BluetoothAddress"/> 喂回去即可。
    /// </param>
    /// <param name="options">中心通道参数（Log 等会被复用；TargetAddress 由工厂注入，调用方无需设置）。</param>
    public static Func<CancellationToken, Task<IByteChannel>> Central(
        Func<ulong?> addressProvider,
        BleGattCentralOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(addressProvider);

        return async cancellationToken =>
        {
            var target = addressProvider();
            var effective = options is null
                ? new BleGattCentralOptions { TargetAddress = target }
                : options with { TargetAddress = target };

            var channel = await BleGattCentralChannel
                .StartAsync(effective, cancellationToken)
                .ConfigureAwait(false);
            return channel;
        };
    }

    /// <summary>
    /// 外设侧工厂：启动广播 → 等首个订阅者（链路建立的标志）。
    /// </summary>
    /// <remarks>
    /// 等待期间对端断开（连接又掉了）按失败处理：关闭通道、抛 <see cref="IOException"/>，
    /// 让监督者走退避重试（重新广播）。等待没有额外超时——外设就该一直广播等对端，
    /// 结束只由监督者的取消令牌驱动。
    /// </remarks>
    public static Func<CancellationToken, Task<IByteChannel>> Peripheral(
        BleGattPeripheralOptions? options = null)
        => async cancellationToken =>
        {
            var channel = await BleGattPeripheralChannel
                .StartAsync(options, cancellationToken)
                .ConfigureAwait(false);

            if (channel.SubscriberCount > 0)
            {
                return channel;
            }

            var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnConnected(object? sender, EventArgs args) => established.TrySetResult();

            void OnDisconnected(object? sender, EventArgs args) => established.TrySetException(
                new IOException("对端连上后又断开，外设侧重新进入等待。"));

            channel.ClientConnected += OnConnected;
            channel.ClientDisconnected += OnDisconnected;

            try
            {
                await established.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return channel;
            }
            catch (OperationCanceledException)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                channel.ClientConnected -= OnConnected;
                channel.ClientDisconnected -= OnDisconnected;
            }
        };
}
