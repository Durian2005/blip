namespace Lixin.Transport;

/// <summary>
/// 一个已观测到的可连接广播：地址 + 信号强度 + 观测时间。
/// </summary>
/// <remarks>
/// 纯数据，不持有任何 WinRT 对象 —— 这样扫描决策可以被单元测试完整覆盖，
/// 而不必依赖真机。同一个地址的多次观测由 <see cref="BleGattScanPolicy"/> 去重。
/// </remarks>
public readonly record struct BleGattScanCandidate(
    ulong Address,
    short Rssi,
    string? Name,
    long ObservedMs);

/// <summary>
/// 中心侧扫描候选的选择策略：去重、保鲜、择优。
///
/// 为什么单独拎出来：扫描本身必须真机才能跑，但「从一堆广播里选哪个去连」是
/// 纯决策逻辑，值得被单元测试钉住 —— 选项必须**确定性**，与项目的竞选哲学一致：
/// <list type="bullet">
/// <item>同一地址多次观测只保留最新一次（去重）；</item>
/// <item>过时（超出保鲜窗口）的观测不参与选择；</item>
/// <item>信号最强者优先；信号并列时取地址较小者（确定性并列仲裁）。</item>
/// </list>
/// </summary>
public sealed class BleGattScanPolicy
{
    private readonly Dictionary<ulong, BleGattScanCandidate> _candidates = new();

    /// <summary>
    /// 记录一次广播观测。同一地址重复出现时覆盖旧值 —— 只留最新，
    /// RSSI 随距离波动，旧值只会误导选择。
    /// </summary>
    public void Observe(ulong address, short rssi, string? name, long nowMs)
    {
        _candidates[address] = new BleGattScanCandidate(address, rssi, name, nowMs);
    }

    /// <summary>
    /// 从保鲜窗口内的候选里选出最佳目标。
    /// </summary>
    /// <param name="freshnessMs">
    /// 保鲜窗口：观测时间距今超过它的候选不参与选择。
    /// 广播是周期性的（典型 100 ms ~ 数百 ms 一次），一个还在广播的设备
    /// 不可能超过几秒没有新观测 —— 超期说明它停了广播或已连给别人。
    /// </param>
    /// <param name="nowMs">当前单调时钟（毫秒）。由调用方传入，保证可测。</param>
    /// <param name="candidate">选出的目标。</param>
    /// <remarks>
    /// 选择不删除记录（同一设备仍会在下轮被选中），但顺手清掉明显过期的条目，
    /// 防止长时间扫描无限累积。过期判定用 8 倍保鲜窗口作宽限：刚出窗的条目
    /// 可能只是广播间隔偏长，还值得留一轮。
    /// </remarks>
    public bool TrySelectBest(int freshnessMs, long nowMs, out BleGattScanCandidate candidate)
    {
        if (freshnessMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(freshnessMs), freshnessMs, "保鲜窗口必须为正数。");
        }

        var found = false;
        candidate = default;

        foreach (var entry in _candidates.Values)
        {
            if (nowMs - entry.ObservedMs > freshnessMs)
            {
                continue;
            }

            if (!found
                || entry.Rssi > candidate.Rssi
                || (entry.Rssi == candidate.Rssi && entry.Address < candidate.Address))
            {
                candidate = entry;
                found = true;
            }
        }

        PruneStale(nowMs, freshnessMs);
        return found;
    }

    /// <summary>当前记录的候选数量（诊断用）。</summary>
    public int Count => _candidates.Count;

    /// <summary>清空全部观测。新一轮扫描开始前调用。</summary>
    public void Reset() => _candidates.Clear();

    private void PruneStale(long nowMs, int freshnessMs)
    {
        var cutoff = nowMs - freshnessMs * 8;
        List<ulong>? stale = null;

        foreach (var pair in _candidates)
        {
            if (pair.Value.ObservedMs < cutoff)
            {
                (stale ??= new List<ulong>()).Add(pair.Key);
            }
        }

        if (stale is not null)
        {
            foreach (var address in stale)
            {
                _candidates.Remove(address);
            }
        }
    }
}
