using System.Diagnostics;
using System.Text;
using Lixin.Core.Election;
using Lixin.Transport;

namespace Lixin.BleChannelProbe;

/// <summary>
/// T3.1 外设侧的真机验收工具（不是产品代码，不进发布包）。
///
/// 它与 <c>Lixin.BleProbe</c> 的分工：
/// <list type="bullet">
/// <item><c>BleProbe</c>（P1a）验的是**这台机器**能不能当 BLE 外设 —— 裸 WinRT，不含产品代码；</item>
/// <item>本工具（T3.1）验的是**这个实现**对不对 —— 直接跑 <see cref="BleGattPeripheralChannel"/>。</item>
/// </list>
///
/// 用法：
/// <code>
///   BleChannelProbe              交互模式（默认，连上后自动跑一遍测试）
///   BleChannelProbe --no-auto    只等连接，所有测试手动触发
///   BleChannelProbe --wait 120   限时 120 秒后自动退出
/// </code>
///
/// 交互键：<c>2</c> 发 4 KB / <c>3</c> 发 32 KB / <c>s</c> 打统计 / <c>q</c> 退出。
///
/// 验收判据见 <c>docs/design-t3.1-gatt-channel.md</c> §8，本工具负责 1/2/3/4/6/7 项
/// （第 5 项需要手机当外设，由 <c>BleGattCentralChannel</c> 那侧验）。
/// </summary>
internal static class Program
{
    /// <summary>META 特征值里报给对端的昵称。</summary>
    private const string Nickname = "lixin-probe";

    /// <summary>快速验证用的数据量：块数少，手机上肉眼能扫一遍。</summary>
    private const int QuickPayloadSize = 4 * 1024;

    /// <summary>PRD 规定的单条消息上限，验证分片在真实上限下不出错。</summary>
    private const int BigPayloadSize = 32 * 1024;

    /// <summary>轮询订阅者数量的间隔。</summary>
    private const int SubscriberPollMs = 200;

    /// <summary>
    /// 测试载荷的原子模式：可读的 16 进制字符。
    /// 用可读字符而不是随机字节，是因为验收要**人眼看手机**——
    /// 手机上看到 "0123456789ABCDEF" 连续循环，就能确认没丢块、没错位；
    /// 换成随机字节，人眼只能看长度，看不出断裂。
    /// </summary>
    private static readonly byte[] _pattern = "0123456789ABCDEF"u8.ToArray();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些终端不支持切换编码，不影响功能
        }

        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"【致命错误】{ex.GetType().Name}: {ex.Message}");
            if (ex.HResult != 0)
            {
                Log($"           HRESULT = 0x{ex.HResult:X8}");
            }

            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var autoRun = !args.Contains("--no-auto");
        var waitSeconds = ParseWaitSeconds(args);

        Log("============ blip · T3.1 外设侧验收 ============");

        // ---------- 广播载荷 ----------
        var key = CampaignKey.NewRandom();
        var serviceData = DiscoveryPayload.Encode(key, RoleCapabilities.Both);
        var metaPayload = BuildMetaPayload();

        Log($"竞选键    : {key}");
        Log($"发现载荷  : {Convert.ToHexString(serviceData)}（{serviceData.Length} B）");
        Log($"META 内容 : {Convert.ToHexString(metaPayload)}（{metaPayload.Length} B）");

        // ---------- 启动通道 ----------
        BleGattPeripheralChannel channel;
        try
        {
            channel = await BleGattPeripheralChannel.StartAsync(new BleGattPeripheralOptions
            {
                MetaPayload = metaPayload,
                ServiceData = serviceData,
                Log = message => Log($"  [通道] {message}"),
            });
        }
        catch (BleGattStartupException ex)
        {
            Log($"【启动失败】{ex.Reason}：{ex.Message}");
            Log(ex.ShouldPersistCapabilityFailure
                ? "            ← 这是能力缺失，T3.4 应写入能力档案"
                : "            ← 临时故障，**不要**写能力档案");
            return 2;
        }

        await using var disposable = channel;

        // 断链事件与接收循环各管一半：事件给界面用，返回 0 给帧层用。
        channel.ClientDisconnected += (_, _) =>
        {
            Log("");
            Log("★★ 事件：中心已断开 —— 上层（T3.3）应据此触发重连");
        };

        using var cts = new CancellationTokenSource();
        var receiver = RunReceiverAsync(channel, cts.Token);

        PrintPhoneInstructions(serviceData);

        // ---------- 等中心订阅 ----------
        // --wait 限制的是**总时长**，等待订阅也算在里面。
        // 否则「--wait 6 却等 180 秒才超时」——自动化跑起来就是这个坑。
        var subscribeTimeout = waitSeconds is null ? 180 : Math.Min(180, waitSeconds.Value);

        Log($"等待中心订阅（最多 {subscribeTimeout} 秒）…");
        var subscribed = await WaitForSubscriberAsync(channel, subscribeTimeout, cts.Token);

        if (!subscribed)
        {
            Log($"【超时】{subscribeTimeout} 秒内没有中心订阅。请检查：手机是否连上、是否点了 RX 的订阅按钮。");
            cts.Cancel();
            await channel.CloseAsync();
            return 3;
        }

        Log("★ 中心已订阅，可以发送了。");
        Log("");

        // ---------- 自动测试 ----------
        if (autoRun)
        {
            await RunBuiltInTestsAsync(channel);
        }
        else
        {
            Log("（--no-auto：自动测试已跳过，用 2 / 3 键手动触发）");
        }

        PrintStats(channel);

        // ---------- 交互 / 限时 ----------
        var exitCode = await RunInteractiveAsync(channel, waitSeconds, cts.Token);

        cts.Cancel();
        try
        {
            await receiver;
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }

        await channel.CloseAsync();
        Log($"结束。累计收 {channel.ReceivedBytes} B，发 {channel.SentBytes} B。");
        return exitCode;
    }

    /// <summary>
    /// 连上后自动跑一遍：短消息 → 4 KB → 32 KB。
    /// 顺序是从小到大，前一档出问题时就地停下，避免拿 32 KB 去掩盖一个 64 B 就能暴露的错。
    /// </summary>
    private static async Task RunBuiltInTestsAsync(BleGattPeripheralChannel channel)
    {
        Log("---------- 自动测试 ----------");

        await Task.Delay(500);
        if (!await SendAsync(channel, BuildPatternPayload(64), "HELLO 64 B（验收项 3：外设→中心）"))
        {
            return;
        }

        await Task.Delay(800);
        if (!await SendAsync(channel, BuildPatternPayload(QuickPayloadSize), "4 KB（验收项 4：分片）"))
        {
            return;
        }

        // 32 KB 提交到协议栈只是瞬间的事，真正传完要按 9–20 KB/s 走 2–4 秒。
        // 所以这里留足时间再打统计，否则读到的是「还没传完」的数字。
        await Task.Delay(1500);
        if (!await SendAsync(channel, BuildPatternPayload(BigPayloadSize), "32 KB（验收项 4：消息上限）"))
        {
            return;
        }

        Log("等待 5 秒让 32 KB 传完（按 9–20 KB/s 约需 2–4 秒）…");
        await Task.Delay(5000);
        Log("");
        Log("☆ 请在手机上核对 32 KB 这条：内容应是从 0 开始连续的 0123456789ABCDEF 循环，");
        Log("  末尾不应出现跳字或截断。首尾各 16 B 见上方「首/尾」打印。");
        Log("");
    }

    /// <summary>
    /// 持续读通道，把收到的字节打出来。
    /// <b>返回 0 就是验收项 6 的判据</b>：断链时必须是 0，不能抛异常、不能永久挂起。
    /// </summary>
    private static async Task RunReceiverAsync(BleGattPeripheralChannel channel, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        long total = 0;
        var chunks = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            int count;
            try
            {
                count = await channel.ReceiveAsync(buffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (count == 0)
            {
                Log("");
                Log($"★★★ ReceiveAsync 返回 0 —— 断链语义正确（验收项 6）");
                Log($"      本次共收 {total} B / {chunks} 块");
                Log("");
                return;
            }

            total += count;
            chunks++;
            var preview = Convert.ToHexString(buffer.AsSpan(0, Math.Min(32, count)));
            Log($"← {count} B（第 {chunks} 块，累计 {total} B） hex[0..{Math.Min(32, count)}] = {preview}");
        }
    }

    private static async Task<int> RunInteractiveAsync(
        BleGattPeripheralChannel channel,
        int? waitSeconds,
        CancellationToken cancellationToken)
    {
        if (waitSeconds is null)
        {
            Log("交互：2 = 发 4 KB　3 = 发 32 KB　s = 统计　q = 退出");
            Log("");
        }
        else
        {
            Log($"限时 {waitSeconds} 秒，到点自动退出（期间手机可以随时断开）。");
            Log("");
        }

        var started = DateTime.UtcNow;
        var lastStatus = DateTime.UtcNow;
        var running = true;

        while (running && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, cancellationToken);

            if (waitSeconds is not null && (DateTime.UtcNow - started).TotalSeconds >= waitSeconds.Value)
            {
                Log("限时结束。");
                break;
            }

            if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 10)
            {
                lastStatus = DateTime.UtcNow;
                if (channel.IsOpen && channel.SubscriberCount > 0)
                {
                    PrintStats(channel);
                }
            }

            // 通道被断链语义关掉后不再接受发送（发送会抛，已有异常处理），
            // 但仍然要响应按键，否则用户就没法按 q 退出了。
            ConsoleKeyInfo key;
            try
            {
                if (!Console.KeyAvailable)
                {
                    continue;
                }

                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // 输入被重定向（没有控制台），退化成「只能靠 --wait 退出」
                continue;
            }

            switch (key.KeyChar)
            {
                case '2':
                    await SendAsync(channel, BuildPatternPayload(QuickPayloadSize), "4 KB");
                    break;
                case '3':
                    await SendAsync(channel, BuildPatternPayload(BigPayloadSize), "32 KB");
                    break;
                case 's' or 'S':
                    PrintStats(channel);
                    break;
                case 'q' or 'Q' or '\u001b':
                    running = false;
                    break;
            }
        }

        return 0;
    }

    private static async Task<bool> SendAsync(
        BleGattPeripheralChannel channel,
        byte[] payload,
        string label)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await channel.SendAsync(payload);
            stopwatch.Stop();

            Log($"→ {label}：{payload.Length} B 已提交，耗时 {stopwatch.ElapsedMilliseconds} ms");
            Log($"   首 16 B = {Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length)))}");
            Log($"   尾 16 B = {Convert.ToHexString(payload.AsSpan(Math.Max(0, payload.Length - 16)))}");
            Log($"   ★ 注意：耗时是「提交到协议栈」的时间，真实空口传输按 9–20 KB/s 另有 2–4 秒，");
            Log($"     所以这个数字**不是**吞吐，别拿它算速率。");
            return true;
        }
        catch (Exception ex)
        {
            Log($"【失败】{label}：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForSubscriberAsync(
        BleGattPeripheralChannel channel,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (channel.SubscriberCount > 0)
            {
                return true;
            }

            if (!channel.IsOpen)
            {
                return false;
            }

            await Task.Delay(SubscriberPollMs, cancellationToken);
        }

        return false;
    }

    private static void PrintStats(BleGattPeripheralChannel channel)
    {
        var mtu = channel.MaxPduSize;

        Log("");
        Log("---------------- 统计 ----------------");
        Log($"MTU（观测值）  : {(mtu > 0 ? mtu.ToString() : "未观测到（中心还没写入过，会按 23 兜底）")}");
        Log($"单块有效载荷   : {BleGattFraming.PayloadSizeFor(mtu)} B");
        Log($"订阅者         : {channel.SubscriberCount}");
        Log($"收到           : {channel.ReceivedBytes} B");
        Log($"发出           : {channel.SentBytes} B");
        Log($"通道状态       : {(channel.IsOpen ? "开启" : "已关闭")}");
        Log("-------------------------------------");
        Log("");
    }

    private static void PrintPhoneInstructions(byte[] serviceData)
    {
        Log("");
        Log("============ 手机上这样做 ============");
        Log($"1. nRF Connect → Scanner → 下拉刷新");
        Log($"2. 找到服务 UUID 为 {BleGattUuids.Service} 的设备");
        Log($"   （iOS 看不到 MAC，只能靠服务 UUID 或计算机名认）");
        Log("3. CONNECT → 展开该服务，应看到 3 个特征值：");
        Log($"   · TX   {BleGattUuids.Tx}  ← 你写这里（验收项 2）");
        Log($"   · RX   {BleGattUuids.Rx}  ← 点 ↓↓↓ 订阅，本工具就从这里通知你（验收项 3/4）");
        Log($"   · META {BleGattUuids.Meta}  ← 点读，应返回 2 字节头 + \"lixin-probe\"");
        Log("4. 先点 RX 的订阅图标（不订阅的话本工具没法发）");
        Log("5. 再往 TX 写点东西，本窗口会打印 ← N B 和 hex（验收项 2）");
        Log("6. 验断链：手机 DISCONNECT，本窗口应打印「ReceiveAsync 返回 0」（验收项 6）");
        Log("");
        Log($"⚠ 本工具也带了 {serviceData.Length} B 的 ServiceData，但 Legacy 广播只有 31 B 预算，");
        Log("  系统会把 ServiceData 丢掉（上面的状态行会显示 StartedWithoutAllAdvertisementData）。");
        Log("  所以 nRF Connect 里看到的设备**大概率没有 Service Data** —— 这是预期行为，不是 bug。");
        Log("  认设备请认服务 UUID 或计算机名。");
        Log("======================================");
        Log("");
    }

    /// <summary>
    /// META 特征值内容：<c>[0]</c> 协议版本、<c>[1]</c> 能力位、<c>[2..]</c> UTF-8 昵称。
    /// 前两字节刻意与 <see cref="DiscoveryPayload"/> 对齐，对端解析时少一套规则。
    /// </summary>
    private static byte[] BuildMetaPayload()
    {
        var nameBytes = Encoding.UTF8.GetBytes(Nickname);
        var payload = new byte[2 + nameBytes.Length];
        payload[0] = 0x01;
        payload[1] = 0x03;
        nameBytes.CopyTo(payload, 2);
        return payload;
    }

    private static byte[] BuildPatternPayload(int size)
    {
        var buffer = new byte[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = _pattern[i % _pattern.Length];
        }

        return buffer;
    }

    private static int? ParseWaitSeconds(string[] args)
    {
        var index = Array.IndexOf(args, "--wait");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds))
        {
            return seconds;
        }

        return null;
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
