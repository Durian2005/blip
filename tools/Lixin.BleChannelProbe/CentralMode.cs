using System.Diagnostics;
using System.Text;
using Lixin.Transport;

namespace Lixin.BleChannelProbe;

/// <summary>
/// T3.1 中心侧的真机验收（设计文档 §8 第 5 项）：电脑当中心，手机 nRF Connect 当外设。
/// 与外设模式（Program）同一套验收判据：64 B / 4 KB / 32 KB 三档、断链 ReceiveAsync 返回 0。
///
/// 用法（在 Program.Main 里由 --central 分流进来）：
/// <code>
///   BleChannelProbe --central            按服务 UUID 扫描 → 连接 → 自动测试
///   BleChannelProbe --central --no-auto  只连不发，测试手动触发（2 / 3 键）
///   BleChannelProbe --central --wait 120 限时 120 秒
/// </code>
///
/// ⚠ iOS 会缓存对端 GATT 表：改过本机服务结构后手机可能看不到变化——
/// 换 Service UUID（--variant）、忽略设备或重启 iPhone（P1a 时期实测）。
/// </summary>
internal static class CentralMode
{
    /// <summary>中心侧默认扫描超时。手机手动配 GATT 服务要时间，比产品默认 15 s 宽。</summary>
    private const int ScanTimeoutSeconds = 60;

    private static readonly byte[] _pattern = "0123456789ABCDEF"u8.ToArray();

    public static async Task<int> RunAsync(string[] args)
    {
        var autoRun = !args.Contains("--no-auto");
        var waitSeconds = ParseWaitSeconds(args);

        Program.Log("============ blip · T3.1 中心侧验收（第 5 项） ============");
        Program.Log("");

        PrintPhoneInstructions();

        BleGattCentralChannel channel;
        try
        {
            channel = await BleGattCentralChannel.StartAsync(new BleGattCentralOptions
            {
                ScanTimeoutMs = ScanTimeoutSeconds * 1000,
                Log = message => Program.Log($"  [通道] {message}"),
            });
        }
        catch (BleGattStartupException ex)
        {
            Program.Log($"【启动失败】{ex.Reason}：{ex.Message}");
            return 2;
        }

        await using var disposable = channel;

        Program.Log($"★ 已连接：{(channel.DeviceName ?? "未知设备")}（{channel.BluetoothAddress:X12}）");
        Program.Log($"★ MTU 观测值：{channel.MaxPduSize}（中心侧连接时即可读到，不等对端首写）");
        Program.Log("");

        channel.ConnectionLost += (_, _) =>
        {
            Program.Log("");
            Program.Log("★★ 事件：ConnectionLost —— 链路断了（T3.3 重连器的触发点）");
        };

        using var cts = new CancellationTokenSource();
        var receiver = RunReceiverAsync(channel, cts.Token);

        if (autoRun)
        {
            await RunBuiltInTestsAsync(channel);
        }
        else
        {
            Program.Log("（--no-auto：自动测试已跳过，用 2 / 3 键手动触发）");
        }

        PrintStats(channel);

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
        Program.Log($"结束。累计收 {channel.ReceivedBytes} B，发 {channel.SentBytes} B。");
        return exitCode;
    }

    /// <summary>与外设模式同一顺序：64 B → 4 KB → 32 KB，从小到大，前一档出错就地停。</summary>
    private static async Task RunBuiltInTestsAsync(BleGattCentralChannel channel)
    {
        Program.Log("---------- 自动测试 ----------");

        await Task.Delay(500);
        if (!await SendAsync(channel, BuildPatternPayload(64), "64 B（验收项 5：中心→外设）"))
        {
            return;
        }

        await Task.Delay(800);
        if (!await SendAsync(channel, BuildPatternPayload(4 * 1024), "4 KB（分片）"))
        {
            return;
        }

        await Task.Delay(1500);
        if (!await SendAsync(channel, BuildPatternPayload(32 * 1024), "32 KB（消息上限）"))
        {
            return;
        }

        Program.Log("等待 5 秒让 32 KB 传完（按 9–20 KB/s 约需 2–4 秒）…");
        await Task.Delay(5000);
        Program.Log("");
        Program.Log("☆ 请在手机上核对 32 KB 这条：内容应是从 0 开始连续的 0123456789ABCDEF 循环。");
        Program.Log("");
    }

    /// <summary>返回 0 = 断链语义正确（与外设模式同一判据）。</summary>
    private static async Task RunReceiverAsync(BleGattCentralChannel channel, CancellationToken cancellationToken)
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
                Program.Log("");
                Program.Log("★★★ ReceiveAsync 返回 0 —— 断链语义正确");
                Program.Log($"      本次共收 {total} B / {chunks} 块");
                Program.Log("");
                return;
            }

            total += count;
            chunks++;
            var preview = Convert.ToHexString(buffer.AsSpan(0, Math.Min(32, count)));
            Program.Log($"← {count} B（第 {chunks} 块，累计 {total} B） hex[0..{Math.Min(32, count)}] = {preview}");
        }
    }

    private static async Task<int> RunInteractiveAsync(
        BleGattCentralChannel channel,
        int? waitSeconds,
        CancellationToken cancellationToken)
    {
        if (waitSeconds is null)
        {
            Program.Log("交互：2 = 发 4 KB　3 = 发 32 KB　s = 统计　q = 退出");
        }
        else
        {
            Program.Log($"限时 {waitSeconds} 秒，到点自动退出。");
        }

        Program.Log("");

        var started = DateTime.UtcNow;
        var running = true;

        while (running && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, cancellationToken);

            if (waitSeconds is not null && (DateTime.UtcNow - started).TotalSeconds >= waitSeconds.Value)
            {
                Program.Log("限时结束。");
                break;
            }

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
                    await SendAsync(channel, BuildPatternPayload(4 * 1024), "4 KB");
                    break;
                case '3':
                    await SendAsync(channel, BuildPatternPayload(32 * 1024), "32 KB");
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

    private static async Task<bool> SendAsync(BleGattCentralChannel channel, byte[] payload, string label)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await channel.SendAsync(payload);
            stopwatch.Stop();

            Program.Log($"→ {label}：{payload.Length} B 已提交，耗时 {stopwatch.ElapsedMilliseconds} ms");
            Program.Log($"   首 16 B = {Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length)))}");
            Program.Log($"   尾 16 B = {Convert.ToHexString(payload.AsSpan(Math.Max(0, payload.Length - 16)))}");
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"【失败】{label}：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void PrintStats(BleGattCentralChannel channel)
    {
        Program.Log("");
        Program.Log("---------------- 统计 ----------------");
        Program.Log($"MTU（观测值）  : {channel.MaxPduSize}");
        Program.Log($"单块有效载荷   : {BleGattFraming.PayloadSizeFor(channel.MaxPduSize)} B");
        Program.Log($"对端地址       : {channel.BluetoothAddress:X12}");
        Program.Log($"收到           : {channel.ReceivedBytes} B");
        Program.Log($"发出           : {channel.SentBytes} B");
        Program.Log($"通道状态       : {(channel.IsOpen ? "开启" : "已关闭")}");
        Program.Log("-------------------------------------");
        Program.Log("");
    }

    /// <summary>
    /// 手机端指引。nRF Connect 配 GATT 服务 + 广播 128 位服务 UUID：
    /// Legacy 广播里 Flags(3 B) + 128 位 UUID(18 B) = 21 B，放得下（P1a 实测），
    /// 电脑的扫描按服务 UUID 硬件过滤，手机广播必须带上它才能被扫到。
    /// </summary>
    private static void PrintPhoneInstructions()
    {
        Program.Log("============ 手机上这样做（nRF Connect） ============");
        Program.Log("一、配 GATT 服务器（Server 标签）");
        Program.Log("1. Server → 右上角 + 新建配置，添加 Service：");
        Program.Log($"   UUID = {BleGattUuids.Service}");
        Program.Log("2. 该 Service 下添加 3 个 Characteristic：");
        Program.Log($"   · TX   {BleGattUuids.Tx}");
        Program.Log("          Properties: Write + Write without response");
        Program.Log($"   · RX   {BleGattUuids.Rx}");
        Program.Log("          Properties: Read + Notify（电脑从这里收通知）");
        Program.Log($"   · META {BleGattUuids.Meta}");
        Program.Log("          Properties: Read；初始值（HEX）= 0103 6C 69 78 69 6E 2D 70 72 6F 62 65");
        Program.Log("          （= 版本 01 + 能力 03 + UTF-8 \"lixin-probe\"）");
        Program.Log("3. 保存配置并启动 Server。");
        Program.Log("");
        Program.Log("二、广播（Advertiser 标签）——必须让电脑扫得到");
        Program.Log("4. Advertiser → + 新建：在 Legacy advertising 里勾选/添加");
        Program.Log($"   「UUID 128」= {BleGattUuids.Service}");
        Program.Log("   （服务器配置与广播是两回事：不广播 UUID，电脑的硬件过滤器看不见你）");
        Program.Log("5. 启动广播（is connectable 打开），然后本窗口会自动连上来。");
        Program.Log("");
        Program.Log("三、验收");
        Program.Log("6. 电脑自动发 64 B / 4 KB / 32 KB → 你往 TX 里看收到的值");
        Program.Log("7. 你往 RX 写点值并触发 Notify → 本窗口打印 ← N B");
        Program.Log("8. 验断链：手机停掉广播/断开 → 本窗口应打印「ReceiveAsync 返回 0」");
        Program.Log("");
        Program.Log($"⚠ 广播预算提醒：Flags(3 B) + 128 位 UUID(18 B) = 21 B，放得下；");
        Program.Log("  但**别**再往广播里塞别的（ServiceData 会把预算挤爆，P1a 实测 57 B > 31 B）。");
        Program.Log("====================================================");
        Program.Log("");
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
}
