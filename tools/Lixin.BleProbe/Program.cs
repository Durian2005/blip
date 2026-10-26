using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace Lixin.BleProbe;

/// <summary>
/// P1a 能力验证工具：让本机作为 BLE 外设（GATT Server + 可连接广播），
/// 用手机上的 nRF Connect 当中心连上来验证双向收发。
///
/// 用法：
///   BleProbe --probe    只做能力探测与 5 秒广播，然后退出（自动化用）
///   BleProbe            进入交互模式，等手机连接；按 Q 退出
/// </summary>
internal static class Program
{
    // 自定义 128 位 UUID，避开蓝牙 SIG 保留的 16 位段，免得被 App 按标准服务解析。
    // ServiceUuid 末字节可用 --variant 改写：iOS 会缓存对端的 GATT 表，
    // 换 UUID 是绕过缓存、判断「到底是缓存还是发布」的最快办法。
    private static Guid ServiceUuid = new("6a1b7c00-5d2e-4f38-9a41-0c7e3b9d5f01");
    private static readonly Guid TxUuid = new("6a1b7c01-5d2e-4f38-9a41-0c7e3b9d5f01");
    private static readonly Guid RxUuid = new("6a1b7c02-5d2e-4f38-9a41-0c7e3b9d5f01");

    private static int _writeCount;
    private static int _notifyCount;

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持，忽略 */ }

        var probeOnly = args.Any(a => a is "--probe" or "-p");

        // --variant xx：只改 Service UUID 的最后一个字节，用来绕过 iOS 的 GATT 缓存
        var variantIndex = Array.IndexOf(args, "--variant");
        if (variantIndex >= 0 && variantIndex + 1 < args.Length)
        {
            var variant = args[variantIndex + 1].PadLeft(2, '0');
            ServiceUuid = new Guid($"6a1b7c00-5d2e-4f38-9a41-0c7e3b9d5f{variant}");
        }

        int? waitSeconds = null;
        var waitIndex = Array.IndexOf(args, "--wait");
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out var seconds))
        {
            waitSeconds = seconds;
        }

        try
        {
            return RunAsync(probeOnly, waitSeconds).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"【致命错误】{ex.GetType().Name}: {ex.Message}");
            if (ex.HResult != 0) Log($"           HRESULT = 0x{ex.HResult:X8}");
            Log("提示：若为 0x80070005(E_ACCESSDENIED)，说明当前进程身份不允许做 GATT Server。");
            return 1;
        }
    }

    private static async Task<int> RunAsync(bool probeOnly, int? waitSeconds)
    {
        Log("================ blip · P1a 外设能力验证 ================");
        Log($"模式：{(probeOnly ? "探测（自动退出）" : waitSeconds is null ? "交互（按 Q 退出）" : $"限时（{waitSeconds} 秒后自动退出）")}");

        // ---------- 1. 适配器与角色能力 ----------
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            Log("【失败】没有找到蓝牙适配器（BluetoothAdapter.GetDefaultAsync 返回 null）。");
            Log("       请确认设备有蓝牙硬件，且没有被禁用。");
            return 2;
        }

        Log("---------- 1. 适配器能力 ----------");
        Log($"本机计算机名        : {Environment.MachineName}（广播里显示的名字大概率就是它）");
        Log($"MAC 地址            : {FormatMac(adapter.BluetoothAddress)}");
        Log($"经典蓝牙(BR/EDR)    : {(adapter.IsClassicSupported ? "支持" : "不支持")}");
        Log($"低功耗(BLE)         : {(adapter.IsLowEnergySupported ? "支持" : "不支持")}");
        Log($"中心角色 Central    : {(adapter.IsCentralRoleSupported ? "支持" : "不支持")}");
        Log($"外设角色 Peripheral : {(adapter.IsPeripheralRoleSupported ? "★ 支持" : "✗ 不支持")}");
        Log($"广播卸载(offload)   : {(adapter.IsAdvertisementOffloadSupported ? "支持" : "不支持")}");

        if (!adapter.IsPeripheralRoleSupported)
        {
            Log("【关键结论】本机适配器不支持 BLE 外设角色 —— R1 风险成立，需改道（RFCOMM 或换适配器）。");
            return 3;
        }

        // ---------- 2. 无线电开关状态 ----------
        Log("---------- 2. 无线电状态 ----------");
        try
        {
            var radio = await adapter.GetRadioAsync();
            Log($"Radio 状态          : {radio.State}（需为 On）");
            if (radio.State != RadioState.On)
            {
                Log("【失败】蓝牙未开启，请先打开蓝牙开关。");
                return 4;
            }
        }
        catch (Exception ex)
        {
            // 未打包的桌面进程可能无权查询 Radio，这不致命
            Log($"Radio 查询失败（不影响继续测试）：{ex.GetType().Name}");
        }

        // ---------- 3. 创建 GATT 服务 ----------
        Log("---------- 3. 创建 GATT 服务 ----------");
        var createResult = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (createResult.Error != BluetoothError.Success)
        {
            Log($"【失败】CreateAsync 返回 {createResult.Error}");
            return 5;
        }

        var provider = createResult.ServiceProvider;
        Log($"Service UUID        : {ServiceUuid}");

        // TX：手机写入这里
        var txParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Write
                                     | GattCharacteristicProperties.WriteWithoutResponse
                                     | GattCharacteristicProperties.Read,
            UserDescription = "blip TX - phone writes here",
            ReadProtectionLevel = GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            // 不用 StaticValue：改由 ReadRequested 里显式回应，这样每次被读都能留下日志
        };
        var txResult = await provider.Service.CreateCharacteristicAsync(TxUuid, txParams);
        if (txResult.Error != BluetoothError.Success)
        {
            Log($"【失败】创建 TX 特征值失败：{txResult.Error}");
            return 6;
        }
        var tx = txResult.Characteristic;
        Log($"TX 特征值（写）     : {TxUuid}");

        // RX：本机通知手机。同时给 Read，因为部分平台的枚举对「全无读权限」的特征值表现异常
        var rxParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify
                                     | GattCharacteristicProperties.Read,
            UserDescription = "blip RX - server notifies here",
            ReadProtectionLevel = GattProtectionLevel.Plain,
        };
        var rxResult = await provider.Service.CreateCharacteristicAsync(RxUuid, rxParams);
        if (rxResult.Error != BluetoothError.Success)
        {
            Log($"【失败】创建 RX 特征值失败：{rxResult.Error}");
            return 7;
        }
        var rx = rxResult.Characteristic;
        Log($"RX 特征值（通知）   : {RxUuid}");

        // 把「实际发布了什么」打出来，避免手机端看不到时无从判断
        Log("---------- 3.1 已发布到 GATT 数据库的内容 ----------");
        Log($"特征值总数          : {provider.Service.Characteristics.Count}（应为 2）");
        foreach (var characteristic in provider.Service.Characteristics)
        {
            Log($"  · {characteristic.Uuid}  [{characteristic.CharacteristicProperties}]");
        }

        // 客户端连上/断开时要有信号，否则无法区分「没连上」和「连上但没订阅」。
        // GattLocalService 没有 Session 属性（那是客户端 GattDeviceService 的），
        // 因此改用「读取事件」作为连接证据：iOS 展开特征值时会主动读一次。

        // ---------- 4. 事件 ----------
        // 读取事件 = 「手机已经连上并且能看到这个服务」的可靠证据
        tx.ReadRequested += async (_, readArgs) =>
        {
            var deferral = readArgs.GetDeferral();
            try
            {
                var request = await readArgs.GetRequestAsync();
                if (request is null) return;
                Log("★ 有客户端读取 TX —— 已连上且服务可见（这一步通了，说明 GATT 没问题）");
                request.RespondWithValue(ToBuffer(Encoding.UTF8.GetBytes("blip-p1a")));
            }
            catch (Exception ex)
            {
                Log($"读事件处理异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        tx.WriteRequested += async (_, writeArgs) =>
        {
            var deferral = writeArgs.GetDeferral();
            try
            {
                var request = await writeArgs.GetRequestAsync();
                if (request is null) return;

                var bytes = new byte[request.Value.Length];
                using (var reader = DataReader.FromBuffer(request.Value))
                {
                    reader.ReadBytes(bytes);
                }

                _writeCount++;
                Log($"[RX #{_writeCount}] 收到 {bytes.Length} 字节 | hex={Convert.ToHexString(bytes)}");
                Log($"          文本 = \"{ToDisplayText(bytes)}\"");

                if (request.Option == GattWriteOption.WriteWithResponse)
                {
                    request.Respond();
                }

                // 立刻回一条，证明「手机→电脑」和「电脑→手机」都通
                await NotifyAsync(rx, Encoding.UTF8.GetBytes($"ECHO[{_writeCount}]:{ToDisplayText(bytes)}"));
            }
            catch (Exception ex)
            {
                Log($"写事件处理异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        rx.ReadRequested += async (_, readArgs) =>
        {
            var deferral = readArgs.GetDeferral();
            try
            {
                var request = await readArgs.GetRequestAsync();
                if (request is null) return;
                Log("★ 有客户端读取 RX —— 已连上且服务可见");
                request.RespondWithValue(ToBuffer(Encoding.UTF8.GetBytes("blip-p1a-rx")));
            }
            catch (Exception ex)
            {
                Log($"RX 读事件异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        rx.SubscribedClientsChanged += (sender, _) =>
        {
            var count = sender.SubscribedClients.Count;
            Log(count > 0
                ? $"★ 已有 {count} 个客户端订阅通知 —— 连接建立成功"
                : "× 订阅者已断开");
        };

        provider.AdvertisementStatusChanged += (_, e) =>
            Log($"广播状态 → {e.Status}" + (e.Error != BluetoothError.Success ? $"（错误：{e.Error}）" : ""));

        // ---------- 5. 开始广播 ----------
        Log("---------- 4. 启动可连接广播 ----------");
        var advParams = new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        };

        try
        {
            provider.StartAdvertising(advParams);
            Log($"StartAdvertising 已调用，当前状态：{provider.AdvertisementStatus}");
        }
        catch (Exception ex)
        {
            Log($"【失败】StartAdvertising 抛异常：{ex.GetType().Name}: {ex.Message}");
            Log($"        HRESULT = 0x{ex.HResult:X8}");
            return 8;
        }

        PrintPhoneInstructions();

        if (probeOnly)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Log($"5 秒后状态：{provider.AdvertisementStatus}（Started 即成功）");
            provider.StopAdvertising();
            Log("已停止广播，探测结束。");
            return provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started ? 0 : 9;
        }

        // ---------- 6. 运行循环 ----------
        var started = DateTime.UtcNow;
        Log(waitSeconds is null
            ? "提示：按 Q 退出。"
            : $"提示：广播将保持 {waitSeconds} 秒，期间请用手机连接。");

        var lastHeartbeat = DateTime.UtcNow;
        var lastStatusLog = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(200);

            if (waitSeconds is not null)
            {
                var remain = waitSeconds.Value - (int)(DateTime.UtcNow - started).TotalSeconds;
                if (remain <= 0)
                {
                    Log("限时结束。");
                    break;
                }

                // 每 20 秒报一次剩余时间与订阅状态，方便判断手机是否已连上
                if (DateTime.UtcNow - lastStatusLog >= TimeSpan.FromSeconds(20))
                {
                    lastStatusLog = DateTime.UtcNow;
                    Log($"…剩余 {remain} 秒，当前订阅者 {rx.SubscribedClients.Count} 个");
                }
            }
            else
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                        {
                            break;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // 输入被重定向时没有控制台，忽略按键检测
                }
            }

            // 每 5 秒给订阅者发一次心跳，验证「电脑→手机」持续可用
            if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(5))
            {
                lastHeartbeat = DateTime.UtcNow;
                if (rx.SubscribedClients.Count > 0)
                {
                    await NotifyAsync(rx, Encoding.UTF8.GetBytes($"PING {DateTime.Now:HH:mm:ss}"));
                }
            }
        }

        provider.StopAdvertising();
        Log($"结束。累计收到写入 {_writeCount} 次，发出通知 {_notifyCount} 次。");
        return 0;
    }

    private static async Task NotifyAsync(GattLocalCharacteristic characteristic, byte[] payload)
    {
        if (characteristic.SubscribedClients.Count == 0)
        {
            Log("（暂无订阅者，跳过通知）");
            return;
        }

        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var results = await characteristic.NotifyValueAsync(writer.DetachBuffer());

        _notifyCount++;
        var ok = results.Count(r => r.Status == GattCommunicationStatus.Success);
        Log($"[TX #{_notifyCount}] 通知 {payload.Length} 字节 → {ok}/{results.Count} 个客户端成功");
    }

    private static void PrintPhoneInstructions()
    {
        Log("");
        Log("================ 接下来在 iPhone 上操作 ================");
        Log("1. 打开 nRF Connect，切到 Scanner 页，下拉刷新");
        Log($"2. 找到名字含这台电脑名、或服务 UUID 为 {ServiceUuid} 的设备");
        Log("   （iOS 看不到 MAC，只能靠名字 / UUID 认）");
        Log("3. 点 CONNECT 连上 → 展开该服务 → 展开 TX 特征值");
        Log("4. 点 TX 右侧的 ↑（Write）图标，输入任意文本，选 UTF-8 提交");
        Log("   → 本窗口应立刻打印 [RX #n]，并且手机 RX 上会收到 ECHO");
        Log("5. 点 RX 右侧的 ↓↓↓（订阅）图标启用通知");
        Log("   → 每 5 秒应收到一条 PING");
        Log("=======================================================");
        Log("");
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static string ToDisplayText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsControl(ch) ? '·' : ch);
        }
        return builder.ToString();
    }

    private static string FormatMac(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
