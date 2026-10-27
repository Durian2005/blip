using Lixin.Core.Election;

namespace Lixin.Tests;

/// <summary>
/// 角色选举的数学性质测试（PRD §7.4.1）。
/// 核心性质只有一条：**双方各自独立决策，结果必须互补**——
/// 一方 Peripheral 则另一方必为 Central，否则就是同时连（撞车）或同时等（死锁）。
/// </summary>
public class RoleElectionTests
{
    private static CampaignKey Key(params byte[] leadingBytes)
    {
        var bytes = new byte[CampaignKey.Size];
        leadingBytes.CopyTo(bytes, 0);
        return CampaignKey.FromBytes(bytes);
    }

    private static CampaignKey RandomKey() => CampaignKey.NewRandom();

    // ---------- 能力对等：竞选键定胜负 ----------

    [Fact]
    public void BothCapable_SmallerKey_BecomesPeripheral()
    {
        var decision = RoleElection.Decide(
            Key(0x00, 0x01), RoleCapabilities.Both,
            Key(0x00, 0x02), RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomePeripheral, decision);
    }

    [Fact]
    public void BothCapable_GreaterKey_BecomesCentral()
    {
        var decision = RoleElection.Decide(
            Key(0x02), RoleCapabilities.Both,
            Key(0x01), RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomeCentral, decision);
    }

    /// <summary>核心性质：任意两个键，双方独立决策得到相反角色。</summary>
    [Fact]
    public void IndependentDecisions_AlwaysComplementary()
    {
        // 固定种子采样足够多的键对，覆盖大小两个方向
        for (var i = 0; i < 200; i++)
        {
            var a = RandomKey();
            var b = RandomKey();
            if (a.CompareTo(b) == 0) continue;

            var aDecision = RoleElection.Decide(a, RoleCapabilities.Both, b, RoleCapabilities.Both);
            var bDecision = RoleElection.Decide(b, RoleCapabilities.Both, a, RoleCapabilities.Both);

            Assert.True(
                (aDecision == ElectionDecision.BecomePeripheral && bDecision == ElectionDecision.BecomeCentral)
                || (aDecision == ElectionDecision.BecomeCentral && bDecision == ElectionDecision.BecomePeripheral),
                $"键对 {a} / {b} 得到 {aDecision} / {bDecision}，不互补 → 会撞车或死锁");
        }
    }

    [Fact]
    public void EqualKeys_BothSidesGetConflictRetry()
    {
        var key = Key(0xAB);
        var a = RoleElection.Decide(key, RoleCapabilities.Both, key, RoleCapabilities.Both);
        var b = RoleElection.Decide(key, RoleCapabilities.Both, key, RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.ConflictRetry, a);
        Assert.Equal(ElectionDecision.ConflictRetry, b);
    }

    // ---------- 能力不对等：硬约束优先，竞选键不参与 ----------

    [Fact]
    public void ICannotAdvertise_AlwaysCentral_EvenWithSmallerKey()
    {
        var decision = RoleElection.Decide(
            Key(0x00), RoleCapabilities.CentralOnly,
            Key(0xFF), RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomeCentral, decision);
    }

    [Fact]
    public void PeerCannotAdvertise_IAlwaysPeripheral_EvenWithLargerKey()
    {
        var decision = RoleElection.Decide(
            Key(0xFF), RoleCapabilities.Both,
            Key(0x00), RoleCapabilities.CentralOnly);
        Assert.Equal(ElectionDecision.BecomePeripheral, decision);
    }

    [Fact]
    public void ICannotConnect_AlwaysPeripheral()
    {
        var decision = RoleElection.Decide(
            Key(0xFF), RoleCapabilities.PeripheralOnly,
            Key(0x01), RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomePeripheral, decision);
    }

    [Fact]
    public void PeerCannotConnect_IAlwaysCentral()
    {
        var decision = RoleElection.Decide(
            Key(0x01), RoleCapabilities.Both,
            Key(0xFF), RoleCapabilities.PeripheralOnly);
        Assert.Equal(ElectionDecision.BecomeCentral, decision);
    }

    // ---------- 拓扑不成立 ----------

    [Fact]
    public void NobodyCanAdvertise_IsImpossible()
    {
        var decision = RoleElection.Decide(
            Key(0x01), RoleCapabilities.CentralOnly,
            Key(0x02), RoleCapabilities.CentralOnly);
        Assert.Equal(ElectionDecision.Impossible, decision);
    }

    [Fact]
    public void NobodyCanConnect_IsImpossible()
    {
        var decision = RoleElection.Decide(
            Key(0x01), RoleCapabilities.PeripheralOnly,
            Key(0x02), RoleCapabilities.PeripheralOnly);
        Assert.Equal(ElectionDecision.Impossible, decision);
    }

    /// <summary>外设角色失败写入档案后，本机等效「只能当中心」，选举随之翻转。</summary>
    [Fact]
    public void PeripheralLearnedUnsupported_ElectionFlipsToCentral()
    {
        var myKey = Key(0x00);   // 键更小，本应 当外设
        var peerKey = Key(0xFF);

        var before = RoleElection.Decide(myKey, RoleCapabilities.Both, peerKey, RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomePeripheral, before);

        var after = RoleElection.Decide(myKey, RoleCapabilities.CentralOnly, peerKey, RoleCapabilities.Both);
        Assert.Equal(ElectionDecision.BecomeCentral, after);
    }

    // ---------- 竞选键本身的性质 ----------

    [Fact]
    public void CampaignKey_LexicographicOrder_LeadingZerosFirst()
    {
        var small = Key(0x00, 0xFF);
        var large = Key(0x01, 0x00);
        Assert.True(small < large);
        Assert.True(large > small);
    }

    [Fact]
    public void CampaignKey_NewRandom_ProducesDistinctKeys()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            Assert.True(seen.Add(Convert.ToHexString(CampaignKey.NewRandom().Value.Span)));
        }
    }

    [Fact]
    public void CampaignKey_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => CampaignKey.FromBytes(new byte[15]));
        Assert.Throws<ArgumentException>(() => CampaignKey.FromBytes(new byte[17]));
    }
}
