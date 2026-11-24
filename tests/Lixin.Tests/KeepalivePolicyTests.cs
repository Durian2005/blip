using Lixin.Core.Connection;

namespace Lixin.Tests;

/// <summary>
/// 保活决策的全部规则（design-t3.3 §4）：空闲发 PING、等待不重复发、超时判死、流量归零。
/// 纯时间戳驱动，不需要真机也不需要定时器。
/// </summary>
public class KeepalivePolicyTests
{
    [Fact]
    public void FreshLink_SaysNothing()
    {
        var policy = new KeepalivePolicy(nowMs: 0);

        Assert.Equal(KeepaliveAction.None, policy.Evaluate(KeepalivePolicy.IdlePingMs - 1));
    }

    [Fact]
    public void IdleBeyond20s_SaysSendPing_ExactlyAtBoundary()
    {
        var policy = new KeepalivePolicy(nowMs: 0);

        Assert.Equal(KeepaliveAction.SendPing, policy.Evaluate(KeepalivePolicy.IdlePingMs));
    }

    [Fact]
    public void PingOutstanding_WithinTimeout_Waits_DoesNotDoubleSend()
    {
        var policy = new KeepalivePolicy(nowMs: 0);
        policy.Evaluate(KeepalivePolicy.IdlePingMs);   // 决策发送
        policy.OnPingSent(KeepalivePolicy.IdlePingMs);

        // 等待窗口内：不能重复发 PING（会淹没对端），也不能提前判死。
        Assert.Equal(KeepaliveAction.None,
            policy.Evaluate(KeepalivePolicy.IdlePingMs + KeepalivePolicy.PongTimeoutMs - 1));
    }

    [Fact]
    public void PingOutstanding_BeyondTimeout_DeclaresDead()
    {
        var policy = new KeepalivePolicy(nowMs: 0);
        policy.OnPingSent(0);

        Assert.Equal(KeepaliveAction.DeclareDead, policy.Evaluate(KeepalivePolicy.PongTimeoutMs));
    }

    [Fact]
    public void PongArrival_CountsAsTraffic_ResetsEverything()
    {
        var policy = new KeepalivePolicy(nowMs: 0);
        policy.OnPingSent(0);

        // PONG 到达 = 流量：未决 PING 清掉、空闲计时归零。
        policy.OnTraffic(KeepalivePolicy.PongTimeoutMs - 1);

        Assert.Equal(KeepaliveAction.None,
            policy.Evaluate(KeepalivePolicy.PongTimeoutMs - 1 + KeepalivePolicy.IdlePingMs - 1));
        Assert.Equal(KeepaliveAction.SendPing,
            policy.Evaluate(KeepalivePolicy.PongTimeoutMs - 1 + KeepalivePolicy.IdlePingMs));
    }

    [Fact]
    public void SendingPing_DoesNotCountAsTraffic()
    {
        // 关键陷阱：如果发 PING 把空闲计时清零，而 PING 丢了又没有 PONG 打破沉寂，
        // 策略会永远停在「等待」前的状态——判死分支根本走不到。
        var policy = new KeepalivePolicy(nowMs: 0);
        policy.OnPingSent(0);

        Assert.Equal(KeepaliveAction.DeclareDead, policy.Evaluate(KeepalivePolicy.PongTimeoutMs));
    }

    [Fact]
    public void OutgoingTraffic_ResetsIdleTimer()
    {
        // 发消息也算流量（链路显然活着），否则发消息的同时还会触发 PING。
        var policy = new KeepalivePolicy(nowMs: 0);
        policy.OnTraffic(1000);

        Assert.Equal(KeepaliveAction.None, policy.Evaluate(1000 + KeepalivePolicy.IdlePingMs - 1));
        Assert.Equal(KeepaliveAction.SendPing, policy.Evaluate(1000 + KeepalivePolicy.IdlePingMs));
    }
}
