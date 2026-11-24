namespace Lixin.Core.Connection;

/// <summary>
/// 重连退避序列：1 → 2 → 4 → 8 → 15 → 15 → … 秒，封顶后恒 15 s。
///
/// 设计依据 <c>docs/design-t3.3-reconnect-keepalive.md</c> §2：
/// <list type="bullet">
/// <item>序列由 WBS 验收口径（走出范围再走回 ≤15 s 恢复）直接给出；</item>
/// <item>**不加随机抖动**——1:1 链路不存在多客户端同步重试，确定性优先；</item>
/// <item>连接成功必须 <see cref="Reset"/>，否则一次长时间断链会让之后每次重连都等满 15 s。</item>
/// </list>
/// 本类是纯计算，不持有定时器——等待由调用方执行（<see cref="DelayFor"/> 只是便捷封装，
/// 测试可注入假延迟）。
/// </summary>
public sealed class ReconnectBackoff
{
    /// <summary>首次失败后的等待（秒）。</summary>
    public const int FirstDelaySeconds = 1;

    /// <summary>封顶值（秒）。WBS 的「≤15 s 恢复」判据由此而来。</summary>
    public const int MaxDelaySeconds = 15;

    private int _attempt;

    /// <summary>当前是第几次连续失败（0 = 从未失败过）。</summary>
    public int Attempt => _attempt;

    /// <summary>
    /// 记录一次失败，返回下一次重连前应等待的毫秒数。
    /// </summary>
    /// <remarks>序列 1000 / 2000 / 4000 / 8000 / 15000 / 15000 …。线程不安全，重连循环是单线程的。</remarks>
    public int RecordFailureAndGetDelayMs()
    {
        _attempt++;
        var seconds = Math.Min(FirstDelaySeconds << (_attempt - 1), MaxDelaySeconds);
        return seconds * 1000;
    }

    /// <summary>连接成功：连续失败计数归零。**必须在每次成功后调用**。</summary>
    public void Reset() => _attempt = 0;

    /// <summary>
    /// 等待下一次重连。生产直接用；测试注入 <paramref name="delayAsync"/> 换成假等待。
    /// </summary>
    public Task DelayFor(int delayMs, CancellationToken cancellationToken, Func<int, CancellationToken, Task>? delayAsync = null)
    {
        if (delayAsync is not null)
        {
            return delayAsync(delayMs, cancellationToken);
        }

        return delayMs <= 0 ? Task.CompletedTask : Task.Delay(delayMs, cancellationToken);
    }
}
