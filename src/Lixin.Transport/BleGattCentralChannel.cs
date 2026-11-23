using Lixin.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Lixin.Transport;

/// <summary>
/// BLE 中心侧通道：本机当 GATT Client，按服务 UUID 扫描外设、连接、订阅通知。
///
/// 设计依据 <c>docs/design-t3.1-gatt-channel.md</c>。本类是 <see cref="IByteChannel"/> 的实现，
/// 因此只负责**字节流**：不加帧头、不做消息边界、不做字节级重传 —— 那些分别是帧层和
/// BLE 链路层已有的事，再做一遍只会翻倍出错面。
///
/// 职责边界：
/// <list type="bullet">
/// <item>发现：按服务 UUID 硬件过滤扫描（FR-03 的发现口径；v1.4 起广播里没有别的载荷）；</item>
/// <item>连接：地址直连 → 服务/特征值发现（Uncached，绕开本机缓存）→ 订阅 RX 通知；</item>
/// <item>发：<see cref="SendAsync"/> → 按 MTU 切块 → <c>WriteValueAsync</c> 写 TX 特征值（WriteWithoutResponse）；</item>
/// <item>收：<c>ValueChanged</c> 回调 → 拷字节 → <see cref="BleGattInbox"/> → 上层拉取；</item>
/// <item>META：连接后读一次，暴露给上层（v1.4 起竞选键从 META 读，消费方是 T3.2/T3.3）。</item>
/// </list>
///
/// 本类不含重连逻辑（T3.3），也不含环境自检（T3.4）。链路断了就断，
/// <see cref="ConnectionLost"/> 与 <see cref="ReceiveAsync"/> 返回 0 是交给上层的两个信号。
/// </summary>
public sealed class BleGattCentralChannel : IByteChannel
{
    /// <summary>写入失败时的退避间隔。</summary>
    public const int WriteRetryDelayMs = 10;

    /// <summary>写入返回 <c>Unreachable</c> 时的最大重试次数。20 × 10 ms ≈ 200 ms，超过即视为链路不可用。</summary>
    public const int WriteMaxRetries = 20;

    /// <summary>扫描结果轮询间隔。</summary>
    public const int ScanPollIntervalMs = 100;

    private readonly BleGattInbox _inbox;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly BleGattScanPolicy _policy = new();
    private readonly Action<string>? _log;

    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _tx;
    private readonly GattCharacteristic _rx;
    private readonly GattCharacteristic? _meta;

    private int _closed;
    private int _lossRaised;
    private int _maxPduSize;
    private int _mtuObserved;
    private long _receivedBytes;
    private long _sentBytes;
    private int _receivedChunks;
    private int _sentChunks;
    private byte[]? _metaPayload;

    private BleGattCentralChannel(
        BluetoothLEDevice device,
        GattSession session,
        GattDeviceService service,
        GattCharacteristic tx,
        GattCharacteristic rx,
        GattCharacteristic? meta,
        BleGattInbox inbox,
        Action<string>? log)
    {
        _device = device;
        _session = session;
        _service = service;
        _tx = tx;
        _rx = rx;
        _meta = meta;
        _inbox = inbox;
        _log = log;
        BluetoothAddress = device.BluetoothAddress;
        DeviceName = string.IsNullOrWhiteSpace(device.Name) ? null : device.Name;
    }

    /// <inheritdoc />
    public string Name => "ble-central";

    /// <summary>
    /// 链路是否可用（已连接且未被关闭）。
    /// </summary>
    /// <remarks>
    /// 中心侧比外设侧多一个可查的状态源：<see cref="BluetoothLEDevice.ConnectionStatus"/>。
    /// 它直接反映链路状态，不需要像外设侧那样从订阅者数量间接推断。
    /// </remarks>
    public bool IsOpen =>
        Volatile.Read(ref _closed) == 0
        && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>对端的蓝牙地址。</summary>
    public ulong BluetoothAddress { get; }

    /// <summary>对端广播名（可能为空；iOS 不广播名字时为 null）。</summary>
    public string? DeviceName { get; }

    /// <summary>
    /// 最近一次观测到的 ATT_MTU。连接建立时即可读到（中心侧有 <see cref="GattSession"/>，
    /// 不像外设侧要等对端先写一块），<c>0</c> 表示尚未观测到。
    /// </summary>
    public int MaxPduSize => Volatile.Read(ref _maxPduSize);

    /// <summary>
    /// 对端 META 特征值的内容（协议版本 + 昵称 + 能力位 + 竞选键，即发现载荷）。
    /// 连接建立后读一次；读取失败时为空（上层按「拿不到 = 信息缺失」保守处理）。
    /// </summary>
    public ReadOnlyMemory<byte> MetaPayload => _metaPayload ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>累计收到的字节数（诊断用，不含明文）。</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    /// <summary>累计发出的字节数（诊断用）。</summary>
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    /// <summary>
    /// 判定「对端已断开」时触发。
    /// </summary>
    /// <remarks>
    /// 触发后通道**会自行关闭**，<see cref="ReceiveAsync"/> 随即返回 0。
    /// **是否重连由 T3.3 决定，通道层不做策略**——上层读到 0 后自行重建通道。
    /// 事件只触发一次；CloseAsync 自己触发的状态变化不会再走这里。
    /// </remarks>
    public event EventHandler? ConnectionLost;

    /// <summary>
    /// 启动中心侧：检查适配器 → 定位对端（直连或扫描）→ 连接 → 服务发现 → 订阅通知 → 读 META。
    /// </summary>
    /// <param name="options">扫描超时、目标地址等参数。</param>
    /// <param name="cancellationToken">
    /// 中断整个启动过程（扫描等待、GATT 异步调用都会响应它）。
    /// </param>
    /// <exception cref="BleGattStartupException">
    /// 无适配器、蓝牙关闭、扫描超时、连接失败、服务/特征值发现失败或订阅失败。
    /// <see cref="BleGattStartupException.Reason"/> 可直接映射成给用户看的提示（T3.4）。
    /// </exception>
    public static async Task<BleGattCentralChannel> StartAsync(
        BleGattCentralOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BleGattCentralOptions();
        options.Validate();
        var log = options.Log;

        // ---------- 1. 适配器与角色能力 ----------
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new BleGattStartupException(BleGattFailureReason.AdapterMissing,
                "没有找到蓝牙适配器。请确认设备有蓝牙硬件且未被禁用。");

        if (!adapter.IsLowEnergySupported)
        {
            throw new BleGattStartupException(BleGattFailureReason.AdapterMissing,
                "本机适配器不支持 BLE。");
        }

        if (!adapter.IsCentralRoleSupported)
        {
            throw new BleGattStartupException(BleGattFailureReason.CentralRoleUnsupported,
                "本机适配器不支持 BLE 中心角色，无法主动发起连接。");
        }

        await BleGattEnvironment.EnsureRadioOnAsync(adapter, log, cancellationToken).ConfigureAwait(false);

        // ---------- 2. 定位对端：直连（T3.3 重连用）或按服务 UUID 扫描 ----------
        var (address, name) = options.TargetAddress is { } direct
            ? (direct, null)
            : await ScanForPeerAsync(options, cancellationToken).ConfigureAwait(false);

        // ---------- 3. 连接与服务发现（全部 Uncached，绕开本机 GATT 缓存） ----------
        // 从蓝牙地址拿设备对象：拿不到说明地址无效或设备不可达。
        // 注意这一步**不会**发起连接——真正的连接发生在第一个 GATT 操作上。
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            .AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new BleGattStartupException(BleGattFailureReason.ConnectionFailed,
                $"无法连接到 {address:X12}：设备不可达或地址无效。");

        var servicesResult = await device
            .GetGattServicesForUuidAsync(BleGattUuids.Service, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);

        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            device.Dispose();
            throw new BleGattStartupException(BleGattFailureReason.ServiceDiscoveryFailed,
                $"服务发现失败（{servicesResult.Status}）：对端 {address:X12} 上找不到离信服务。");
        }

        var service = servicesResult.Services[0];

        var characteristicsResult = await service
            .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);

        if (characteristicsResult.Status != GattCommunicationStatus.Success)
        {
            device.Dispose();
            throw new BleGattStartupException(BleGattFailureReason.ServiceDiscoveryFailed,
                $"特征值发现失败（{characteristicsResult.Status}）。");
        }

        GattCharacteristic? tx = null, rx = null, meta = null;
        foreach (var characteristic in characteristicsResult.Characteristics)
        {
            if (characteristic.Uuid == BleGattUuids.Tx)
            {
                tx = characteristic;
            }
            else if (characteristic.Uuid == BleGattUuids.Rx)
            {
                rx = characteristic;
            }
            else if (characteristic.Uuid == BleGattUuids.Meta)
            {
                meta = characteristic;
            }
        }

        if (tx is null || rx is null)
        {
            device.Dispose();
            throw new BleGattStartupException(BleGattFailureReason.ServiceDiscoveryFailed,
                $"对端的特征值不完整：TX={tx is not null}，RX={rx is not null}（META 可选）。"
                + "多半是对端版本不符或 iOS 的 GATT 缓存——让对端重启广播后重试。");
        }

        // ---------- 4. GATT 会话：MTU 的唯一入口 ----------
        var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)
            .AsTask(cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            device.Dispose();
            throw new BleGattStartupException(BleGattFailureReason.ConnectionFailed,
                "无法建立 GATT 会话（GattSession 为空）。");
        }

        // MaintainConnection：链路上没有未完成的 GATT 操作时，协议栈可能主动断开空闲链路。
        // 聊天会话要长期保持，显式钉住；CloseAsync 里恢复 false 再释放。
        session.MaintainConnection = true;
        log?.Invoke($"GATT 会话已建立：MaxPduSize={session.MaxPduSize}");

        var inbox = new BleGattInbox(options.InboxCapacity);
        var channel = new BleGattCentralChannel(
            device, session, service, tx, rx, meta, inbox, log);

        // ---------- 5. 事件：全部挂在收尾动作之前，避免半初始化状态被回调撞上 ----------
        device.ConnectionStatusChanged += channel.OnConnectionStatusChanged;
        session.MaxPduSizeChanged += channel.OnMaxPduSizeChanged;
        channel.ObserveMtu(session.MaxPduSize);

        // ---------- 6. 订阅通知（外设 → 中心的数据通道） ----------
        rx.ValueChanged += channel.OnValueChanged;

        var cccdStatus = await rx
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (cccdStatus != GattCommunicationStatus.Success)
        {
            await channel.CloseAsync().ConfigureAwait(false);
            throw new BleGattStartupException(BleGattFailureReason.SubscribeFailed,
                $"订阅 RX 通知失败（{cccdStatus}）。");
        }

        log?.Invoke("已订阅 RX 通知（外设 → 中心的数据通道就绪）");

        // ---------- 7. 读 META：竞选键/能力位的来源（v1.4：不再放广播） ----------
        if (meta is not null)
        {
            await channel.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        }

        log?.Invoke(
            $"中心通道就绪：对端 {(name is null ? address.ToString("X12") : $"{name} ({address:X12})")}，"
            + $"MTU={channel.MaxPduSize}，META={channel.MetaPayload.Length} B");

        return channel;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await _inbox.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// 并发调用由内部信号量串行化，顺序按调用到达顺序保证（<see cref="IByteChannel"/> 的契约）。
    /// 链路未连接时抛 <see cref="InvalidOperationException"/>：静默丢弃会让帧层少一段字节、
    /// 解析出错位帧，比报错难查得多。
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

            var payloadSize = BleGattFraming.PayloadSizeFor(MaxPduSize);
            var chunks = BleGattFraming.Slice(data, payloadSize);

            foreach (var chunk in chunks)
            {
                await WriteChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
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
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        // 让后续 ReceiveAsync 读到 0：已入队但未读走的数据仍会先被读出来，这是有意为之。
        _inbox.Complete();

        // 礼貌退订：不写 None 的话对端可能继续发通知（被系统丢弃），浪费对端的发送预算。
        try
        {
            await _rx
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 关闭路径上的失败不值得中断收尾（对端可能已经消失）。
            _log?.Invoke($"退订通知失败（收尾阶段忽略）：{ex.GetType().Name}");
        }

        _rx.ValueChanged -= OnValueChanged;
        _session.MaxPduSizeChanged -= OnMaxPduSizeChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        // 先解除连接钉住再释放，否则协议栈可能还试图维持一条死链。
        _session.MaintainConnection = false;
        _session.Dispose();
        _device.Dispose();
        _service.Dispose();

        _log?.Invoke($"中心通道已关闭（收 {ReceivedBytes} B / 发 {SentBytes} B）");
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

    private async ValueTask WriteChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var bytes = chunk.ToArray();   // WinRT 要求 IBuffer，这一跳的拷贝无法避免

        for (var attempt = 0; ; attempt++)
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            var buffer = writer.DetachBuffer();

            var status = await _tx
                .WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (status == GattCommunicationStatus.Success)
            {
                return;
            }

            // Unreachable 可能是瞬断，也可能是协议栈暂时收不下：
            // 退避后重试，最多 200 ms。其余状态（ProtocolError / AccessDenied）直接放弃。
            if (status == GattCommunicationStatus.Unreachable && attempt < WriteMaxRetries)
            {
                await Task.Delay(WriteRetryDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 其它状态或重试耗尽：断链，绝不跳过这一块继续写。
            // 跳过会在字节流中间挖洞，帧层会解析出错位帧，
            // 现象是「偶尔消息内容损坏」—— 最难定位的一类故障。宁可断线，不要错位。
            _log?.Invoke($"写入失败（{status}，重试 {attempt} 次），主动断开：已发 {SentBytes} B");
            await CloseAsync().ConfigureAwait(false);
            throw new IOException($"BLE 写入失败：{status}，重试 {attempt} 次后放弃。");
        }
    }

    // ------------------------------------------------------------------
    // 接收（系统线程回调 —— 这里的每条约束都来自 design §4）
    // ------------------------------------------------------------------

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        // 拷字节必须在这个回调内完成：CharacteristicValue 的 IBuffer 只在回调期间有效。
        var value = args.CharacteristicValue;
        var bytes = new byte[value.Length];
        using (var reader = DataReader.FromBuffer(value))
        {
            reader.ReadBytes(bytes);
        }

        if (!_inbox.TryPost(bytes))
        {
            // 溢出 = 即将丢字节。断链是唯一不破坏字节流的选项。
            _log?.Invoke($"接收缓冲溢出（累计 {_inbox.PostedBytes} B），主动断开以免字节流错位");
            _ = Task.Run(CloseAsync);
            return;
        }

        Interlocked.Add(ref _receivedBytes, bytes.Length);
        _receivedChunks++;

        // 日志只记长度：验收要求日志不含明文消息内容。
        _log?.Invoke($"← {bytes.Length} B（第 {_receivedChunks} 块，累计 {ReceivedBytes} B）");
    }

    // ------------------------------------------------------------------
    // 链路状态
    // ------------------------------------------------------------------

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
        {
            return;
        }

        // CloseAsync 自己触发的断开（以及 Dispose 链上的二次回调）不该触发 ConnectionLost。
        if (Interlocked.Exchange(ref _lossRaised, 1) != 0)
        {
            return;
        }

        _log?.Invoke("对端已断开（ConnectionStatus → Disconnected）");
        ConnectionLost?.Invoke(this, EventArgs.Empty);

        // 必须让 ReceiveAsync 返回 0：这是 IByteChannel 的断链契约（返回 0 = 对端已关闭）。
        // CloseAsync 是幂等的，且收尾里有异步 WinRT 调用，不能占着系统回调线程做。
        _ = Task.Run(CloseAsync);
    }

    private void ObserveMtu(int mtu)
    {
        if (mtu <= 0)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _maxPduSize, mtu);

        if (Interlocked.Exchange(ref _mtuObserved, 1) == 0)
        {
            _log?.Invoke($"MTU 观测：MaxPduSize={mtu} → 单块有效载荷 {BleGattFraming.PayloadSizeFor(mtu)} B");
        }
        else if (previous != mtu)
        {
            _log?.Invoke($"MTU 变化：{previous} → {mtu}，单块调整为 {BleGattFraming.PayloadSizeFor(mtu)} B");
        }
    }

    private void OnMaxPduSizeChanged(GattSession sender, object args)
    {
        ObserveMtu(sender.MaxPduSize);
    }

    private async Task ReadMetaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _meta!.ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (result.Status != GattCommunicationStatus.Success)
            {
                _log?.Invoke($"META 读取失败（{result.Status}），按空处理（上层保守决策）");
                return;
            }

            var bytes = new byte[result.Value.Length];
            using (var reader = DataReader.FromBuffer(result.Value))
            {
                reader.ReadBytes(bytes);
            }

            _metaPayload = bytes;
            _log?.Invoke($"META 已读取：{bytes.Length} B");
        }
        catch (Exception ex)
        {
            // META 是元信息，不是数据通道。读不到不该让连接失败——
            // 上层（T3.2/T3.3）按「拿不到 = 信息缺失」的保守语义处理。
            _log?.Invoke($"META 读取异常（不阻断连接）：{ex.GetType().Name}");
        }
    }

    // ------------------------------------------------------------------
    // 扫描
    // ------------------------------------------------------------------

    private static async Task<(ulong Address, string? Name)> ScanForPeerAsync(
        BleGattCentralOptions options,
        CancellationToken cancellationToken)
    {
        var log = options.Log;
        var policy = new BleGattScanPolicy();

        // 硬件级过滤：只把包含我们服务 UUID 的广播交给回调，省电且日志干净。
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AdvertisementFilter = CreateServiceFilter(),
        };

        watcher.Received += (_, args) =>
        {
            // 硬件过滤器之外的双保险：nRF Connect 等工具的广播有时不带完整字段。
            if (!args.Advertisement.ServiceUuids.Contains(BleGattUuids.Service))
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName)
                ? null
                : args.Advertisement.LocalName;
            policy.Observe(args.BluetoothAddress, args.RawSignalStrengthInDBm, name, Environment.TickCount64);
        };

        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            throw new BleGattStartupException(BleGattFailureReason.DiscoveryFailed,
                $"启动扫描失败：{ex.GetType().Name}: {ex.Message}");
        }

        log?.Invoke($"开始扫描（按服务 UUID {BleGattUuids.Service} 过滤，超时 {options.ScanTimeoutMs} ms）");

        try
        {
            var deadline = Environment.TickCount64 + options.ScanTimeoutMs;

            while (true)
            {
                if (policy.TrySelectBest(options.ScanFreshnessMs, Environment.TickCount64, out var candidate))
                {
                    log?.Invoke(
                        $"发现离信外设：{candidate.Address:X12}"
                        + (candidate.Name is null ? string.Empty : $"（{candidate.Name}）")
                        + $"，RSSI={candidate.Rssi} dBm，候选 {policy.Count} 个");
                    return (candidate.Address, candidate.Name);
                }

                if (Environment.TickCount64 >= deadline)
                {
                    throw new BleGattStartupException(BleGattFailureReason.DiscoveryFailed,
                        $"扫描超时（{options.ScanTimeoutMs} ms），没有发现离信外设。"
                        + "请确认对端已启动且广播中。");
                }

                await Task.Delay(ScanPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch (Exception ex)
            {
                // 停止失败只影响省电，不影响结果。
                log?.Invoke($"停止扫描时异常（忽略）：{ex.GetType().Name}");
            }
        }
    }

    private static BluetoothLEAdvertisementFilter CreateServiceFilter()
    {
        var advertisement = new BluetoothLEAdvertisement();
        advertisement.ServiceUuids.Add(BleGattUuids.Service);
        return new BluetoothLEAdvertisementFilter { Advertisement = advertisement };
    }
}

/// <summary>中心通道的启动参数。</summary>
public sealed record BleGattCentralOptions
{
    /// <summary>诊断日志出口。要求调用方保证**不记录明文消息内容**。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>接收缓冲块数上限，生产用默认值。</summary>
    public int InboxCapacity { get; init; } = BleGattInbox.DefaultCapacity;

    /// <summary>扫描超时（毫秒）。超时抛 <see cref="BleGattStartupException"/>（Reason = DiscoveryFailed）。</summary>
    public int ScanTimeoutMs { get; init; } = 15000;

    /// <summary>广播观测的保鲜窗口（毫秒），见 <see cref="BleGattScanPolicy.TrySelectBest"/>。</summary>
    public int ScanFreshnessMs { get; init; } = 2000;

    /// <summary>
    /// 直连目标地址。设置后跳过扫描直接连接 —— 给 T3.3 的重连用：
    /// 断线重连时地址已知，再扫一遍既慢又可能扫到别的实例。
    /// </summary>
    public ulong? TargetAddress { get; init; }

    internal void Validate()
    {
        if (InboxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InboxCapacity), InboxCapacity, "接收缓冲容量必须为正数。");
        }

        if (ScanTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScanTimeoutMs), ScanTimeoutMs, "扫描超时必须为正数。");
        }

        if (ScanFreshnessMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScanFreshnessMs), ScanFreshnessMs, "保鲜窗口必须为正数。");
        }
    }
}
