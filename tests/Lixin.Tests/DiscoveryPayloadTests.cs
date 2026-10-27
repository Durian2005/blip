using Lixin.Core.Election;

namespace Lixin.Tests;

/// <summary>
/// 广播发现载荷的编解码测试。
/// 这个载荷是双方建立连接**之前**交换的唯一信息，坏了选举就没了输入。
/// 扫描时会把它周围所有设备的广播都喂进来，所以「解码失败是常态」——
/// 对垃圾数据必须安静地返回 false，绝不能抛异常。
/// </summary>
public class DiscoveryPayloadTests
{
    [Fact]
    public void RoundTrip_PreservesKeyAndCapabilities()
    {
        var key = CampaignKey.NewRandom();

        var ok = DiscoveryPayload.TryDecode(
            DiscoveryPayload.Encode(key, RoleCapabilities.Both),
            out var decoded,
            out var capabilities);

        Assert.True(ok);
        Assert.Equal(0, key.CompareTo(decoded));
        Assert.True(capabilities.CanAdvertise);
        Assert.True(capabilities.CanConnect);
    }

    [Fact]
    public void RoundTrip_CentralOnly_FlagsSurvive()
    {
        var ok = DiscoveryPayload.TryDecode(
            DiscoveryPayload.Encode(CampaignKey.NewRandom(), RoleCapabilities.CentralOnly),
            out _,
            out var capabilities);

        Assert.True(ok);
        Assert.False(capabilities.CanAdvertise);
        Assert.True(capabilities.CanConnect);
    }

    [Fact]
    public void Payload_IsExactly18Bytes()
    {
        var payload = DiscoveryPayload.Encode(CampaignKey.NewRandom(), RoleCapabilities.Both);
        Assert.Equal(DiscoveryPayload.Size, payload.Length);
        Assert.Equal(18, payload.Length);   // 必须塞进 Legacy 广播的 31 字节限额，写死防止无意膨胀
        Assert.Equal(0x01, payload[0]);     // 版本
    }

    [Theory]
    [InlineData(0)]     // 空数据
    [InlineData(5)]     // 常见错误：把普通厂商数据喂进来
    [InlineData(17)]    // 差一个字节
    [InlineData(19)]    // 多一个字节
    [InlineData(64)]    // 严重超长
    public void WrongLength_ReturnsFalse(int length)
    {
        var ok = DiscoveryPayload.TryDecode(new byte[length], out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void WrongVersion_ReturnsFalse()
    {
        var payload = DiscoveryPayload.Encode(CampaignKey.NewRandom(), RoleCapabilities.Both);
        payload[0] = 0x02;   // 未来版本

        var ok = DiscoveryPayload.TryDecode(payload, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void UnknownCapabilityBits_ReturnsFalse()
    {
        var payload = DiscoveryPayload.Encode(CampaignKey.NewRandom(), RoleCapabilities.Both);
        payload[1] = 0xFF;   // 带有未知位，说明对端版本更新

        var ok = DiscoveryPayload.TryDecode(payload, out _, out _);
        Assert.False(ok);
    }

    /// <summary>端到端串一遍：A、B 各自编码广播 → 互相解码 → 各自决策 → 结果必须互补。</summary>
    [Fact]
    public void FullSimulation_TwoPeersElectComplementaryRoles()
    {
        var keyA = CampaignKey.NewRandom();
        var keyB = CampaignKey.NewRandom();

        // 双方都往广播里塞自己的载荷（这就是 T3.1 里 BluetoothLEAdvertisementPublisher 要发的内容）
        var advertisementA = DiscoveryPayload.Encode(keyA, RoleCapabilities.Both);
        var advertisementB = DiscoveryPayload.Encode(keyB, RoleCapabilities.Both);

        // 双方各自扫到对方
        DiscoveryPayload.TryDecode(advertisementB, out var keyFromB, out var capsB);
        DiscoveryPayload.TryDecode(advertisementA, out var keyFromA, out var capsA);

        var decisionA = RoleElection.Decide(keyA, RoleCapabilities.Both, keyFromB, capsB);
        var decisionB = RoleElection.Decide(keyB, RoleCapabilities.Both, keyFromA, capsA);

        Assert.NotEqual(ElectionDecision.ConflictRetry, decisionA);
        Assert.NotEqual(ElectionDecision.Impossible, decisionA);
        Assert.NotEqual(decisionA, decisionB);   // 一方外设，另一方必为中心
        Assert.Contains(decisionA, new[] { ElectionDecision.BecomePeripheral, ElectionDecision.BecomeCentral });
        Assert.Contains(decisionB, new[] { ElectionDecision.BecomePeripheral, ElectionDecision.BecomeCentral });
    }
}
