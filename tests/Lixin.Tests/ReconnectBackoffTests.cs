using Lixin.Core.Connection;

namespace Lixin.Tests;

/// <summary>
/// 退避序列是 WBS 验收口径的直接实现（≤15 s 恢复），序列错了重连节奏就错了——钉死。
/// </summary>
public class ReconnectBackoffTests
{
    [Fact]
    public void Sequence_Is_1_2_4_8_15_15()
    {
        var backoff = new ReconnectBackoff();

        var delays = new[] { 1000, 2000, 4000, 8000, 15000, 15000, 15000 };
        foreach (var expected in delays)
        {
            Assert.Equal(expected, backoff.RecordFailureAndGetDelayMs());
        }
    }

    [Fact]
    public void Reset_AfterSuccess_BackToFirstDelay()
    {
        var backoff = new ReconnectBackoff();
        backoff.RecordFailureAndGetDelayMs();
        backoff.RecordFailureAndGetDelayMs();
        backoff.RecordFailureAndGetDelayMs();
        Assert.Equal(3, backoff.Attempt);

        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
        Assert.Equal(1000, backoff.RecordFailureAndGetDelayMs());
    }

    [Fact]
    public void DefaultDelayAsync_IsRealTaskDelay()
    {
        var backoff = new ReconnectBackoff();

        // 便捷封装只是转调 Task.Delay：0 与负数不等待，正数真的等。
        Assert.True(backoff.DelayFor(0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.False(backoff.DelayFor(30, CancellationToken.None).IsCompleted);
    }
}
