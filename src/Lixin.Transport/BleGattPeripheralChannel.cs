using Lixin.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Lixin.Transport;

/// <summary>
/// BLE 外设侧通道：本机当 GATT Server，等中心（手机 / 另一台电脑）连上来。
///
/// 设计依据 <c>docs/design-t3.1-gatt-channel.md</c>。本类是 <see cref="IByteChannel"/> 的实现，
/// 因此只负责**字节流**：不加帧头、不做消息边界、不做字节级重传 —— 那些分别是帧层和
/// BLE 链路层已有的事，再做一遍只会翻倍出错面。
///
/// 职责边界：
/// <list type="bullet">
/// <item>发：<see cref="SendAsync"/> → 按 MTU 切块 → <c>NotifyValueAsync</c> 推 RX 特征值；</item>
/// <item>收：<c>WriteRequested</c> 回调 → 拷字节 → <see cref="BleGattInbox"/> → 上层拉取；</item>
/// <item>广播：可连接广播 + 服务 UUID（v1.4：Legacy 广播 31 B 装不下 ServiceData，
/// 发现只靠 UUID；竞选键由对端连接后从 META 特征值读取）。</item>
/// </list>
///
/// 本类不含重连逻辑（T3.3），也不含环境自检（T3.4）。链路断了就断，
/// <see cref="ClientDisconnected"/> 与 <see cref="ReceiveAsync"/> 返回 0 是交给上层的两个信号。
/// </summary>
public sealed class BleGattPeripheralChannel : IByteChannel
{
    /// <summary>通知失败时的退避间隔。</summary>
    public const int NotifyRetryDelayMs = 10;

    /// <summary>通知返回 <c>Unreachable</c> 时的最大重试次数。20 × 10 ms ≈ 200 ms，超过即视为链路不可用。</summary>
    public const int NotifyMaxRetries = 20;

    private readonly Action<string>? _log;
    private readonly BleGattInbox _inbox;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly bool _alwaysRespondToWrite;
    private readonly GattServiceProvider _provider;
    private readonly GattLocalCharacteristic _rxCharacteristic;

    private int _closed;
    private int _maxPduSize;
    private int _mtuObserved;
    private int _subscriberCount;
    private long _receivedBytes;
    private long _sentBytes;
    private int _receivedChunks;
    private int _sentChunks;

    private BleGattPeripheralChannel(
        GattServiceProvider provider,
        GattLocalCharacteristic rxCharacteristic,
        BleGattInbox inbox,
        bool alwaysRespondToWrite,
        Action<string>? log)
    {
        _provider = provider;
        _rxCharacteristic = rxCharacteristic;
        _inbox = inbox;
        _alwaysRespondToWrite = alwaysRespondToWrite;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "ble-peripheral";

    /// <summary>
    /// 服务是否已启动且未被关闭。
    /// </summary>
    /// <remarks>
    /// 这里**不**把「有订阅者」算进来。原因：中心可能已连接但还没订阅，
    /// 若此时报 false，上层会把它当成断线；而外设侧真正的断线信号是
    /// <see cref="ClientDisconnected"/> 与 <see cref="ReceiveAsync"/> 返回 0。
    /// 订阅者数量需要的话读 <see cref="SubscriberCount"/>。
    /// </remarks>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>当前订阅通知的客户端数量。0 表示中心还没开始收，此时发送会失败。</summary>
    public int SubscriberCount => Volatile.Read(ref _subscriberCount);

    /// <summary>
    /// 最近一次观测到的 ATT_MTU。<c>0</c> 表示尚未观测到（此时发送按规范默认值 23 兜底）。
    /// </summary>
    /// <remarks>
    /// WinRT 没有「请求指定 MTU」的 API，只能从 <c>GattWriteRequestedEventArgs.Session</c>
    /// 被动读取。所以这个值要等中心发来第一块数据后才有。诊断用。
    /// </remarks>
    public int MaxPduSize => Volatile.Read(ref _maxPduSize);

    /// <summary>累计收到的字节数（诊断用，不含明文）。</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    /// <summary>累计发出的字节数（诊断用）。</summary>
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    /// <summary>
    /// 判定「中心已断开」时触发。
    /// </summary>
    /// <remarks>
    /// 触发条件是订阅者数量**从大于 0 变成 0**——单看「当前为 0」会把
    /// 「已连接但还没订阅」误判成断开。
    /// 触发后通道**会自行关闭**，<see cref="ReceiveAsync"/> 随即返回 0：
    /// 「永久挂在 ReceiveAsync 上等一个不会来的数据」不是可用的断线语义。
    /// **是否重连由 T3.3 决定，通道层不做策略**——上层读到 0 后自行重建通道。
    /// </remarks>
    public event EventHandler? ClientDisconnected;

    /// <summary>
    /// 判定「中心已连上并订阅」时触发（订阅者 0 → &gt;0）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ClientDisconnected"/> 对称。外设侧的「链路已建立」就是这个时刻——
    /// T3.3 的监督者工厂靠它区分「广播已启动」与「链路可用」：广播只是待命，
    /// 首个订阅者出现才算连接成功。
    /// </remarks>
    public event EventHandler? ClientConnected;

    /// <summary>
    /// 启动外设侧：检查适配器能力 → 注册服务与特征值 → 开始可连接广播。
    /// </summary>
    /// <exception cref="BleGattStartupException">
    /// 无适配器、蓝牙关闭、不支持外设角色、服务注册失败或广播启动失败。
    /// <see cref="BleGattStartupException.Reason"/> 可直接映射成给用户看的提示（T3.4）。
    /// </exception>
    public static async Task<BleGattPeripheralChannel> StartAsync(
        BleGattPeripheralOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BleGattPeripheralOptions();
        options.Validate();
        var log = options.Log;

        // ---------- 1. 适配器与角色能力：静态可查，不做运行时试错 ----------
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new BleGattStartupException(BleGattFailureReason.AdapterMissing,
                "没有找到蓝牙适配器。请确认设备有蓝牙硬件且未被禁用。");

        if (!adapter.IsLowEnergySupported)
        {
            throw new BleGattStartupException(BleGattFailureReason.AdapterMissing,
                "本机适配器不支持 BLE。");
        }

        if (!adapter.IsPeripheralRoleSupported)
        {
            throw new BleGattStartupException(BleGattFailureReason.PeripheralRoleUnsupported,
                "本机适配器不支持 BLE 外设角色，无法作为被连接方。");
        }

        await BleGattEnvironment.EnsureRadioOnAsync(adapter, log, cancellationToken).ConfigureAwait(false);

        // ---------- 2. 注册服务 ----------
        var createResult = await GattServiceProvider
            .CreateAsync(BleGattUuids.Service)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (createResult.Error != BluetoothError.Success)
        {
            throw new BleGattStartupException(BleGattFailureReason.ServiceRegistrationFailed,
                $"GATT 服务注册失败：{createResult.Error}。");
        }

        var provider = createResult.ServiceProvider;
        var inbox = new BleGattInbox(options.InboxCapacity);

        // TX：中心 → 外设。Read 一并给出，因为部分平台枚举「无任何读权限」的特征值时表现异常。
        var txResult = await provider.Service.CreateCharacteristicAsync(
            BleGattUuids.Tx,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write
                                         | GattCharacteristicProperties.WriteWithoutResponse
                                         | GattCharacteristicProperties.Read,
                UserDescription = "lixin TX - central writes here",
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            }).AsTask(cancellationToken).ConfigureAwait(false);

        if (txResult.Error != BluetoothError.Success)
        {
            throw new BleGattStartupException(BleGattFailureReason.ServiceRegistrationFailed,
                $"TX 特征值创建失败：{txResult.Error}。");
        }

        // RX：外设 → 中心，走通知。同样附带 Read，理由同上。
        var rxResult = await provider.Service.CreateCharacteristicAsync(
            BleGattUuids.Rx,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify
                                         | GattCharacteristicProperties.Read,
                UserDescription = "lixin RX - peripheral notifies here",
                ReadProtectionLevel = GattProtectionLevel.Plain,
            }).AsTask(cancellationToken).ConfigureAwait(false);

        if (rxResult.Error != BluetoothError.Success)
        {
            throw new BleGattStartupException(BleGattFailureReason.ServiceRegistrationFailed,
                $"RX 特征值创建失败：{rxResult.Error}。");
        }

        // META：静态只读元信息。用 StaticValue 而不是事件，是因为它连接期间不变，
        // 少一个回调就少一处「系统线程里写错」的机会。
        var metaResult = await provider.Service.CreateCharacteristicAsync(
            BleGattUuids.Meta,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                UserDescription = "lixin meta - protocol version, nickname, capabilities",
                ReadProtectionLevel = GattProtectionLevel.Plain,
                StaticValue = ToBuffer(options.MetaPayload.Span),
            }).AsTask(cancellationToken).ConfigureAwait(false);

        if (metaResult.Error != BluetoothError.Success)
        {
            throw new BleGattStartupException(BleGattFailureReason.ServiceRegistrationFailed,
                $"META 特征值创建失败：{metaResult.Error}。");
        }

        var channel = new BleGattPeripheralChannel(
            provider,
            rxResult.Characteristic,
            inbox,
            options.AlwaysRespondToWrite,
            log);

        // ---------- 3. 事件：全部挂在创建完成之后，避免半初始化状态被回调撞上 ----------
        txResult.Characteristic.WriteRequested += channel.OnWriteRequested;
        rxResult.Characteristic.SubscribedClientsChanged += channel.OnSubscribedClientsChanged;
        provider.AdvertisementStatusChanged += channel.OnAdvertisementStatusChanged;

        log?.Invoke($"GATT 服务已注册：{BleGattUuids.Service}，特征值 3 个（TX/RX/META）");

        // ---------- 4. 开始广播 ----------
        var advertising = new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        };

        // 自定义服务 UUID 会由系统自动写进广播；ServiceData 只额外携带发现载荷。
        if (!options.ServiceData.IsEmpty)
        {
            advertising.ServiceData = ToBuffer(options.ServiceData.Span);
        }

        try
        {
            provider.StartAdvertising(advertising);
        }
        catch (Exception ex) when (ex is not BleGattStartupException)
        {
            throw new BleGattStartupException(BleGattFailureReason.AdvertisingFailed,
                $"开始广播失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 广播状态是异步落定的：实测 StartAdvertising 返回时读到的还是瞬态 Aborted，
        // 约 80–100 ms 后才变成 Started / StartedWithoutAllAdvertisementData。
        // 这里等一下再读，否则会把正常启动过程当成失败（本轮实测踩到的坑）。
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);

        var finalStatus = provider.AdvertisementStatus;
        log?.Invoke($"广播状态确认：{finalStatus}{ExplainAdvertisementStatus(finalStatus)}");
        return channel;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await _inbox.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// 并发调用由内部信号量串行化，顺序按调用到达顺序保证（<see cref="IByteChannel"/> 的契约）。
    /// 无订阅者时抛 <see cref="InvalidOperationException"/>：外设侧没有订阅者就发不出去，
    /// 静默丢弃会让帧层少一段字节、解析出错位帧，比报错难查得多。
    /// </remarks>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);

        if (data.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(!IsOpen, this);

            if (_rxCharacteristic.SubscribedClients.Count == 0)
            {
                throw new InvalidOperationException("外设侧没有订阅者，通知发不出去。");
            }

            var payloadSize = BleGattFraming.PayloadSizeFor(MaxPduSize);
            var chunks = BleGattFraming.Slice(data, payloadSize);

            foreach (var chunk in chunks)
            {
                await NotifyChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _sentBytes, chunk.Length);
                _sentChunks++;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return Task.CompletedTask;
        }

        // 先停广播：停掉之后不会有新的连接/写入进来，剩下的只是收尾。
        try
        {
            _provider.StopAdvertising();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // 蓝牙被关掉时 StopAdvertising 可能抛。这里已经在关闭路径上，不值得中断收尾。
            _log?.Invoke($"停止广播时异常（收尾阶段忽略）：{ex.GetType().Name}");
        }

        // 让后续 ReceiveAsync 读到 0：已入队但未读走的数据仍会先被读出来，这是有意为之。
        _inbox.Complete();
        _log?.Invoke($"外设通道已关闭（收 {ReceivedBytes} B / 发 {SentBytes} B）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 发送
    // ------------------------------------------------------------------

    private async ValueTask NotifyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var bytes = chunk.ToArray();   // WinRT 要求 IBuffer，这一跳的拷贝无法避免

        for (var attempt = 0; ; attempt++)
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            var buffer = writer.DetachBuffer();

            var results = await _rxCharacteristic
                .NotifyValueAsync(buffer)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var status = WorstStatus(results);

            if (status == GattCommunicationStatus.Success)
            {
                return;
            }

            // Unreachable 可能是瞬断，也可能是协议栈暂时收不下：
            // 退避后重试，最多 200 ms。其余状态（ProtocolError / AccessDenied）直接放弃。
            if (status == GattCommunicationStatus.Unreachable && attempt < NotifyMaxRetries)
            {
                await Task.Delay(NotifyRetryDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 其它状态（Unreachable / ProtocolError / AccessDenied）或重试耗尽：
            // 断链，绝不跳过这一块继续写。跳过会在字节流中间挖洞，帧层会解析出错位帧，
            // 现象是「偶尔消息内容损坏」—— 最难定位的一类故障。宁可断线，不要错位。
            _log?.Invoke($"通知失败（{status}，重试 {attempt} 次），主动断开：已发 {SentBytes} B");
            await CloseAsync().ConfigureAwait(false);
            throw new IOException($"BLE 通知失败：{status}，重试 {attempt} 次后放弃。");
        }
    }

    /// <summary>
    /// 多个订阅者的通知结果取「最差」：任何一个失败就按失败处理。
    /// </summary>
    private static GattCommunicationStatus WorstStatus(IReadOnlyList<GattClientNotificationResult> results)
    {
        // 空列表 = 没有任何订阅者收到，等同于不可达
        if (results.Count == 0)
        {
            return GattCommunicationStatus.Unreachable;
        }

        var worst = GattCommunicationStatus.Success;
        foreach (var result in results)
        {
            if (result.Status != GattCommunicationStatus.Success)
            {
                worst = result.Status;
                break;
            }
        }

        return worst;
    }

    // ------------------------------------------------------------------
    // 接收（系统线程回调 —— 这里的每条约束都来自 design §4）
    // ------------------------------------------------------------------

    private void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        // 不在系统线程上跑业务逻辑：起一个后台任务，立刻返回。
        // deferral 必须由处理完成的那个任务来 Complete，否则系统会一直等。
        var deferral = args.GetDeferral();
        _ = HandleWriteAsync(args, deferral);
    }

    private async Task HandleWriteAsync(GattWriteRequestedEventArgs args, Deferral deferral)
    {
        try
        {
            // GetRequestAsync 是 WinRT 规定的取 request 方式，必须 await。
            // ConfigureAwait(false) 是硬要求：WPF 下若续行回到 UI 线程，
            // 这段每块数据都跑一遍的代码会直接把界面拖死。
            var request = await args.GetRequestAsync().AsTask().ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            // ★ 留白 3 的答案：外设侧拿 MTU 的唯一途径就是这里的 Session。
            ObserveMtu(args.Session);

            var value = request.Value;
            var bytes = new byte[value.Length];
            using (var reader = DataReader.FromBuffer(value))
            {
                reader.ReadBytes(bytes);
            }

            // 到这里字节已经拷出。IBuffer 只在回调期间有效，之后不能再碰。

            if (!_inbox.TryPost(bytes))
            {
                // 溢出 = 即将丢字节。断链是唯一不破坏字节流的选项。
                _log?.Invoke($"接收缓冲溢出（累计 {_inbox.PostedBytes} B），主动断开以免字节流错位");
                CompleteWriteRequest(request);
                await CloseAsync().ConfigureAwait(false);
                return;
            }

            CompleteWriteRequest(request);

            Interlocked.Add(ref _receivedBytes, bytes.Length);
            _receivedChunks++;

            // 日志只记长度：验收要求日志不含明文消息内容。
            _log?.Invoke($"← {bytes.Length} B（第 {_receivedChunks} 块，累计 {ReceivedBytes} B）");
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _log?.Invoke($"写请求处理异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// 应答写请求。
    /// </summary>
    /// <remarks>
    /// ★ 留白 2 的处置：WinRT 文档要求 <c>Respond()</c> 必须调用才能算处理完成；
    /// 而 P1 实测（BleProbe）只在 <c>WriteWithResponse</c> 时调用，iPhone 写入照样成功 ——
    /// 说明 <c>WriteWithoutResponse</c> 下不调用也不致命。两种证据冲突时选**更符合契约**的一侧：
    /// 默认应答，并用 <see cref="BleGattPeripheralOptions.AlwaysRespondToWrite"/> 留退路。
    /// 应答失败只记日志，不影响已经入队的数据 —— 那是另一条路径上的事。
    /// </remarks>
    private void CompleteWriteRequest(GattWriteRequest request)
    {
        if (request.Option != GattWriteOption.WriteWithResponse && !_alwaysRespondToWrite)
        {
            return;
        }

        try
        {
            request.Respond();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // 请求可能已被系统自动完成。数据已经进 inbox，不值得为这个失败中断链路。
            _log?.Invoke($"写请求应答失败（数据已入队）：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 观测 MTU。只在第一次拿到有效值后订阅变化事件，避免每次写入都重复挂处理器。
    /// </summary>
    private void ObserveMtu(GattSession? session)
    {
        if (session is null)
        {
            return;
        }

        var mtu = session.MaxPduSize;
        if (mtu <= 0)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _maxPduSize, mtu);

        if (Interlocked.Exchange(ref _mtuObserved, 1) == 0)
        {
            _log?.Invoke($"MTU 观测：MaxPduSize={mtu} → 单块有效载荷 {BleGattFraming.PayloadSizeFor(mtu)} B");
            session.MaxPduSizeChanged += OnMaxPduSizeChanged;
        }
        else if (previous != mtu)
        {
            _log?.Invoke($"MTU 变化：{previous} → {mtu}，单块调整为 {BleGattFraming.PayloadSizeFor(mtu)} B");
        }
    }

    private void OnMaxPduSizeChanged(GattSession sender, object args)
    {
        var mtu = sender.MaxPduSize;
        Interlocked.Exchange(ref _maxPduSize, mtu);
        _log?.Invoke($"MTU 变化事件：MaxPduSize={mtu} → 单块 {BleGattFraming.PayloadSizeFor(mtu)} B");
    }

    /// <summary>
    /// 订阅者变化。
    /// </summary>
    /// <remarks>
    /// ★ 留白 1 的处置：P1 实测确认 <c>SubscribedClientsChanged</c> 能可靠反映中心连上/断开，
    /// 因此用它作为断线信号，不再另找 <c>GattSession</c>。
    /// 注意判定条件是「从大于 0 变成 0」：中心可能连上了但还没订阅，那不是断开。
    /// </remarks>
    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        var count = sender.SubscribedClients.Count;
        var previous = Interlocked.Exchange(ref _subscriberCount, count);

        if (count > 0)
        {
            _log?.Invoke($"中心已订阅通知：{count} 个客户端（可以开始发送）");
            if (previous == 0)
            {
                ClientConnected?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _log?.Invoke("订阅者归零");
        }

        if (previous > 0 && count == 0)
        {
            _log?.Invoke("中心已断开（订阅者 1 → 0）");
            ClientDisconnected?.Invoke(this, EventArgs.Empty);

            // 必须让 ReceiveAsync 返回 0：这是 IByteChannel 的断链契约（返回 0 = 对端已关闭），
            // 只发事件不关 inbox 会让它永久挂起，上层永远等不到断线信号。
            // Complete 本身是 O(1) 非阻塞的，在系统线程上调用安全。
            _inbox.Complete();

            // 停广播会走 WinRT 同步调用，可能慢，不能占着系统回调线程做。
            _ = Task.Run(CloseAsync);
        }
    }

    private void OnAdvertisementStatusChanged(
        GattServiceProvider sender,
        GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        var suffix = args.Error == BluetoothError.Success ? string.Empty : $"（错误：{args.Error}）";
        _log?.Invoke($"广播状态 → {args.Status}{suffix}{ExplainAdvertisementStatus(args.Status)}");
    }

    /// <summary>
    /// 把广播状态翻译成可操作的信息。
    /// </summary>
    /// <remarks>
    /// <c>StartedWithoutAllAdvertisementData</c> 最关键：它**不报错**，只是悄悄少发一部分数据，
    /// 现象是「手机能扫到设备，但 ServiceData 是空的」。实测（2026-11-01）带 18 B ServiceData
    /// 启动时必然进入这个状态，原因是 Legacy 广播的 31 B 预算根本装不下：
    /// Flags 3 B + 128 位 UUID AD 18 B = 21 B，只剩 10 B，而「带 128 位 UUID 的 ServiceData」
    /// 单项就要 2 + 16 + 18 = 36 B。所以要靠服务 UUID 发现，竞选键改从 META 读。
    ///
    /// <c>Aborted</c> 则是**启动瞬态**：实测 <c>StartAdvertising</c> 之后约 80–100 ms 内会先报
    /// <c>Aborted</c>，随后自动重试落到 <c>Started</c> 或 <c>StartedWithoutAllAdvertisementData</c>。
    /// 所以这里不能把它说成「无法被搜到」——那会把正常的启动过程误报成故障。
    /// </remarks>
    private static string ExplainAdvertisementStatus(GattServiceProviderAdvertisementStatus status)
        => status switch
        {
            GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData =>
                " ⚠ 广播包超出 31 B 上限，系统丢弃了部分内容（几乎肯定是 ServiceData）。"
                + "设备仍可被按服务 UUID 搜到；竞选键请让对端改读 META 特征值。",
            GattServiceProviderAdvertisementStatus.Aborted =>
                " （启动瞬态；若 200 ms 后仍停在这里才是真失败）",
            _ => string.Empty,
        };

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static IBuffer ToBuffer(ReadOnlySpan<byte> bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes.ToArray());
        return writer.DetachBuffer();
    }
}

/// <summary>外设通道的启动参数。</summary>
public sealed record BleGattPeripheralOptions
{
    /// <summary>
    /// META 特征值的内容（协议版本 + 昵称 + 能力位）。连接建立前对端读它即可判断兼容性。
    /// </summary>
    public ReadOnlyMemory<byte> MetaPayload { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// 塞进广播的 <c>DiscoveryPayload</c>（18 B）。为空则不写 ServiceData，只靠服务 UUID 被识别。
    /// </summary>
    public ReadOnlyMemory<byte> ServiceData { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>诊断日志出口。要求调用方保证**不记录明文消息内容**。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>接收缓冲块数上限，生产用默认值。</summary>
    public int InboxCapacity { get; init; } = BleGattInbox.DefaultCapacity;

    /// <summary>
    /// 对 <c>WriteWithoutResponse</c> 也调用 <c>Respond()</c>。
    /// </summary>
    /// <remarks>
    /// 默认 true：WinRT 文档要求写请求必须应答，实测不应答也能跑（P1），
    /// 两者冲突时选符合契约的一侧。若真机上发现应答导致异常或降速，
    /// 置 false 并在此记录实测结论。
    /// </remarks>
    public bool AlwaysRespondToWrite { get; init; } = true;

    internal void Validate()
    {
        if (InboxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InboxCapacity), InboxCapacity, "接收缓冲容量必须为正数。");
        }
    }
}

/// <summary>通道启动失败的分类。给 T3.4 做用户可读提示用（外设/中心共用）。</summary>
public enum BleGattFailureReason
{
    /// <summary>没有蓝牙适配器，或适配器不支持 BLE。</summary>
    AdapterMissing,

    /// <summary>蓝牙开关未打开。</summary>
    BluetoothOff,

    /// <summary>本机适配器不支持外设角色 —— 这是能力缺失，应写入能力档案。</summary>
    PeripheralRoleUnsupported,

    /// <summary>本机适配器不支持中心角色（能广播不能主动连）。</summary>
    CentralRoleUnsupported,

    /// <summary>GATT 服务或特征值注册失败。</summary>
    ServiceRegistrationFailed,

    /// <summary>广播启动失败。</summary>
    AdvertisingFailed,

    /// <summary>扫描失败或超时，没有发现离信外设（中心侧）。</summary>
    DiscoveryFailed,

    /// <summary>连接失败：地址不可达、无法建立 GATT 会话等（中心侧）。</summary>
    ConnectionFailed,

    /// <summary>服务/特征值发现失败：对端没有完整的 TX/RX（中心侧）。</summary>
    ServiceDiscoveryFailed,

    /// <summary>订阅通知失败：CCCD 写入未成功（中心侧）。</summary>
    SubscribeFailed,
}

/// <summary>通道启动失败。外设/中心共用，<see cref="Reason"/> 区分具体环节。</summary>
public sealed class BleGattStartupException : Exception
{
    /// <summary>创建一个启动失败异常。</summary>
    public BleGattStartupException(BleGattFailureReason reason, string message)
        : base(message)
        => Reason = reason;

    /// <summary>失败分类。</summary>
    public BleGattFailureReason Reason { get; }

    /// <summary>
    /// 可直接唤起的系统设置页 URI（T3.4）。目前只有「蓝牙开关关着」会填
    /// <c>ms-settings:bluetooth</c>；其余失败原因没有对应的设置页，为 null。
    /// </summary>
    public string? SettingsUri { get; init; }

    /// <summary>
    /// 是否应当写入能力档案。
    /// </summary>
    /// <remarks>
    /// 只有「本机根本做不到外设角色」才写。链路质量、蓝牙开关这类临时状态一律不写——
    /// 否则一次信号不好就会让本机永久放弃外设资格（T3.2 的能力档案注释里记着这个坑）。
    /// </remarks>
    public bool ShouldPersistCapabilityFailure => Reason == BleGattFailureReason.PeripheralRoleUnsupported;
}
