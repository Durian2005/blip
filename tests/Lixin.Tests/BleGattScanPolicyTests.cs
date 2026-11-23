using Lixin.Transport;

namespace Lixin.Tests;

/// <summary>
/// 扫描候选选择的**全部决策逻辑**——这是中心侧唯一不需要真机就能测的部分，
/// 而且直接决定「连到谁」。选项必须确定性，测试把它钉死：
/// 最强信号优先、并列取小地址、过时观测不参与。
/// </summary>
public class BleGattScanPolicyTests
{
    private const int Freshness = 2000;

    [Fact]
    public void EmptyPolicy_SelectsNothing()
    {
        var policy = new BleGattScanPolicy();

        Assert.False(policy.TrySelectBest(Freshness, nowMs: 1000, out var candidate));
        Assert.Equal(default, candidate);
    }

    [Fact]
    public void StrongestRssi_Wins()
    {
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0x11, rssi: -70, name: "weak", nowMs: 1000);
        policy.Observe(address: 0x22, rssi: -45, name: "strong", nowMs: 1000);
        policy.Observe(address: 0x33, rssi: -60, name: "mid", nowMs: 1000);

        Assert.True(policy.TrySelectBest(Freshness, nowMs: 1100, out var candidate));
        Assert.Equal(0x22UL, candidate.Address);
        Assert.Equal("strong", candidate.Name);
    }

    [Fact]
    public void EqualRssi_TieBreaksOnSmallerAddress()
    {
        // 确定性并列仲裁：与竞选键的哲学一致，绝不"先到先得"。
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0xABCD, rssi: -50, name: "bigger", nowMs: 1000);
        policy.Observe(address: 0x1234, rssi: -50, name: "smaller", nowMs: 1000);

        Assert.True(policy.TrySelectBest(Freshness, nowMs: 1100, out var candidate));
        Assert.Equal(0x1234UL, candidate.Address);
        Assert.Equal("smaller", candidate.Name);
    }

    [Fact]
    public void SameAddress_OnlyLatestObservationCounts()
    {
        // RSSI 随距离波动，旧观测只会误导选择——同地址覆盖旧值。
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0x11, rssi: -40, name: "old", nowMs: 1000);
        policy.Observe(address: 0x11, rssi: -80, name: "new", nowMs: 1500);

        Assert.True(policy.TrySelectBest(Freshness, nowMs: 1600, out var candidate));
        Assert.Equal(0x11UL, candidate.Address);
        Assert.Equal(-80, candidate.Rssi);
        Assert.Equal("new", candidate.Name);
    }

    [Fact]
    public void StaleObservation_IsNotSelected()
    {
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0x11, rssi: -40, name: "gone", nowMs: 1000);

        // 距观测 2001 ms，超出 2000 ms 保鲜窗口：还在广播的设备不可能这么久没有新观测。
        Assert.False(policy.TrySelectBest(Freshness, nowMs: 3001, out _));
    }

    [Fact]
    public void FreshCandidate_CoexistsWithStaleOne_StaleIgnored()
    {
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0x11, rssi: -90, name: "stale", nowMs: 500);
        policy.Observe(address: 0x22, rssi: -75, name: "fresh", nowMs: 1500);

        Assert.True(policy.TrySelectBest(Freshness, nowMs: 2000, out var candidate));
        Assert.Equal(0x22UL, candidate.Address);
    }

    [Fact]
    public void Pruning_RemovesLongExpiredEntries_ButKeepsRecent()
    {
        // 8 倍保鲜窗口是宽限：刚出窗的条目留一轮，远超的清掉，防止长扫描无限累积。
        var policy = new BleGattScanPolicy();
        policy.Observe(address: 0x11, rssi: -40, name: "ancient", nowMs: 0);
        policy.Observe(address: 0x22, rssi: -40, name: "slightly-stale", nowMs: 15000);

        // now=17000：0x11（17000 > 16000 宽限线）应被清走，0x22（宽限内）保留。
        policy.TrySelectBest(Freshness, nowMs: 17000, out _);

        Assert.Equal(1, policy.Count);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var policy = new BleGattScanPolicy();
        policy.Observe(0x11, -40, "a", 1000);
        policy.Observe(0x22, -50, "b", 1000);

        policy.Reset();

        Assert.Equal(0, policy.Count);
        Assert.False(policy.TrySelectBest(Freshness, 1100, out _));
    }

    [Fact]
    public void NonPositiveFreshness_Throws()
    {
        var policy = new BleGattScanPolicy();
        policy.Observe(0x11, -40, "a", 1000);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.TrySelectBest(freshnessMs: 0, nowMs: 1100, out _));
    }
}
