using Lixin.Core.Transport;

namespace Lixin.Core.Connection;

/// <summary>
/// 链路监督者：失链 → 指数退避 → 重建通道，成功/断开以事件上报。
///
/// 职责边界（design-t3.3 §1，这条边界是整个设计的支点）：
/// 本类**不读通道数据**——<c>IByteChannel</c> 是单读者契约，读字节的永远是会话泵。
/// 监督者只管理链路生命周期。失链由数据层发现（泵读到 0、保活判死、SendAsync 抛
/// IOException、Ble 断链事件）后调用 <see cref="ReportLinkLost"/> 上报，监督者接管重建。
///
/// 角色无关（design-t3.3 §3）：中心「直连重试」与外设「重启广播等对端回来」
/// 在这里统一成一个 connect 工厂委托 + 同一套退避循环。换通道实现（如 R1 备胎
/// RFCOMM）时本类零改动。
///
/// 事件语义：
/// <list type="bullet">
/// <item><see cref="Connected"/>：一条链路建立完成（首次或重连），参数是新通道；</item>
/// <item><see cref="Disconnected"/>：**已建立的**链路失联（每次失联恰好一次）。
///   启动阶段的连接失败不触发它——没有建立过就谈不上"断开"，上层从
///   Connected 迟迟不来即可推断，避免事件序列里出现重复的假断开。</item>
/// </list>
///
/// 线程模型：内部一个重连循环任务（单线程）；公开方法线程安全，循环内状态不共享。
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IByteChannel>> _connect;
    private readonly ConnectionSupervisorOptions _options;
    private readonly Action<string>? _log;
    private readonly ReconnectBackoff _backoff = new();

    private CancellationTokenSource? _shutdown;
    private TaskCompletionSource? _linkLost;
    private IByteChannel? _current;
    private int _running;
    private Task? _loop;

    /// <param name="connect">
    /// 建立一条**已就绪**链路的工厂。对中心是 <c>BleGattCentralChannel.StartAsync</c>；
    /// 对外设是「启动广播 + 等首个订阅者」。抛异常 = 本次尝试失败，进入退避。
    /// </param>
    /// <param name="options">延迟注入（测试用）、日志出口等。</param>
    public ConnectionSupervisor(
        Func<CancellationToken, Task<IByteChannel>> connect,
        ConnectionSupervisorOptions? options = null)
    {
        _connect = connect;
        _options = options ?? new ConnectionSupervisorOptions();
        _options.Validate();
        _log = _options.Log;
    }

    /// <summary>当前链路。断链后到重连成功前为 null。</summary>
    public IByteChannel? Current => _current;

    /// <summary>监督循环是否在运行（Start 之后、Stop 之前）。</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>当前连续失败次数（诊断用，连接成功后归零）。</summary>
    public int ConsecutiveFailures => _backoff.Attempt;

    /// <summary>一条链路建立完成（首次或重连）。参数是新通道。</summary>
    public event EventHandler<IByteChannel>? Connected;

    /// <summary>已建立的链路失联（每次恰好一次；启动失败不触发）。</summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// 启动监督：立即做第 0 次连接尝试（不走退避），失败则进入退避循环，持续重试。
    /// 幂等：已在运行时直接返回。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown is not null, this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _shutdown = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 上报链路失联。幂等：循环正挂在等待失联信号上，这里放行信号并触发
    /// <see cref="Disconnected"/>；重复调用、或失联的不是当前通道（旧通道的迟到事件）都直接忽略。
    /// </summary>
    /// <remarks>
    /// 没建立过链路（<see cref="Current"/> 为 null）时上报无效——没有建立就谈不上"断开"，
    /// 这正是 <see cref="Disconnected"/> 事件语义（只表示**已建立的**链路失联）的守卫。
    /// </remarks>
    public void ReportLinkLost(IByteChannel? channel)
    {
        if (channel is not null && !ReferenceEquals(channel, _current))
        {
            // 迟到的旧通道事件：重连已经换过通道了，别把新链路误判成又断了。
            return;
        }

        if (Volatile.Read(ref _current) is null)
        {
            return;
        }

        _log?.Invoke("链路失联上报已受理，重连流程接管");
        Disconnected?.Invoke(this, EventArgs.Empty);
        Interlocked.Exchange(ref _linkLost, null)?.TrySetResult();
    }

    /// <summary>停止监督并关闭当前通道。幂等。</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) != 1)
        {
            return;
        }

        _shutdown?.Cancel();

        if (_loop is not null)
        {
            // 循环里的退避等待与工厂调用都挂在这个 token 上，会很快退出。
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 预期的停止路径。
            }

            _loop = null;
        }

        await CloseCurrentAsync().ConfigureAwait(false);
        _backoff.Reset();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown?.Dispose();
        _shutdown = null;
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 重连循环（单任务）
    // ------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken shutdown)
    {
        // 第 0 次连接：立即执行，不走退避（design-t3.3 §2）。
        var attempt = 0;

        while (true)
        {
            shutdown.ThrowIfCancellationRequested();

            var channel = await TryConnectAsync(shutdown).ConfigureAwait(false);
            if (channel is not null)
            {
                Volatile.Write(ref _current, channel);
                _backoff.Reset();
                _log?.Invoke($"链路已建立（第 {attempt} 次尝试，连续失败 {_backoff.Attempt}）");
                Connected?.Invoke(this, channel);

                // 挂起循环等失联信号（事件驱动，不轮询）；StopAsync 的取消会从等待中抛出。
                // 注意：回调捕获**局部变量**——字段在信号被消费后会被置 null，
                // 取消回调若恰好落在那个窗口会 NRE（真踩过）。
                var linkLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _linkLost = linkLost;
                using (shutdown.Register(() => linkLost.TrySetCanceled(shutdown)))
                {
                    await linkLost.Task.ConfigureAwait(false);
                }

                await CloseCurrentAsync().ConfigureAwait(false);
                attempt = 0;
                continue;
            }

            // 连接失败：退避后重试。shutdown 取消会从 Delay 中抛出，直接结束循环。
            attempt++;
            var delayMs = _backoff.RecordFailureAndGetDelayMs();
            _log?.Invoke($"第 {attempt} 次连接失败，{delayMs} ms 后重试");
            await _backoff.DelayFor(delayMs, shutdown, _options.DelayAsync).ConfigureAwait(false);
        }
    }

    private async Task<IByteChannel?> TryConnectAsync(CancellationToken shutdown)
    {
        try
        {
            return await _connect(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // 停止监督不是"连接失败"，让循环直接退出。
        }
        catch (Exception ex)
        {
            _log?.Invoke($"连接尝试失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task CloseCurrentAsync()
    {
        var channel = Interlocked.Exchange(ref _current, null);
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 关闭路径上的失败不值得中断重连（通道可能已经死了）。
            _log?.Invoke($"关闭旧通道时异常（忽略）：{ex.GetType().Name}");
        }
    }
}

/// <summary>监督者的参数。</summary>
public sealed record ConnectionSupervisorOptions
{
    /// <summary>诊断日志出口。要求调用方保证**不记录明文消息内容**。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// 退避等待的注入点。生产为 null（真实 Task.Delay）；测试注入假等待，
    /// 否则 1/2/4/8/15 s 的序列会把测试拖到几十秒。
    /// </summary>
    public Func<int, CancellationToken, Task>? DelayAsync { get; init; }

    internal void Validate()
    {
        // 目前全部参数可选，没有可校验的非法值；留这个钩子与其它 Options 对齐，
        // 后续加约束（如重试上限）时不用改调用方。
    }
}
