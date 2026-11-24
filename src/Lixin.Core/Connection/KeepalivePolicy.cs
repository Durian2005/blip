namespace Lixin.Core.Connection;

/// <summary>保活策略的决策结果。</summary>
public enum KeepaliveAction
{
    /// <summary>什么都不做。</summary>
    None,

    /// <summary>空闲超时，发送 PING 探测对端。</summary>
    SendPing,

    /// <summary>PING 已发但超时无响应，链路判死——调用方应上报失联（重连器接管）。</summary>
    DeclareDead,
}

/// <summary>
/// 应用层保活的**决策**逻辑：回答「现在该不该发 PING / 该不该判死」。
///
/// 为什么是纯逻辑而不是定时器（design-t3.3 §1）：会话泵是唯一读字节的地方，
/// 保活决策由泵在事件循环里顺带评估——泵收到任何帧、发出任何帧时调
/// <see cref="OnTraffic"/>，每轮循环调一次 <see cref="Evaluate"/>。
/// 这让保活不需要第二个读通道的组件（<c>IByteChannel</c> 单读者契约），
/// 也不需要真实的定时器（单测直接喂时间戳）。
///
/// 规则（对称，两侧同一套，PRD §7.5 v1.5 修订）：
/// <list type="bullet">
/// <item>距上次任何流量 ≥ <see cref="IdlePingMs"/> 且无未决 PING → 发 PING；</item>
/// <item>已发 PING 且 &lt; <see cref="PongTimeoutMs"/> → 等待；</item>
/// <item>已发 PING 且 ≥ <see cref="PongTimeoutMs"/> 无任何流量 → 判死；</item>
/// <item>任何收发（含 PONG）归零计时。</item>
/// </list>
/// </summary>
public sealed class KeepalivePolicy
{
    /// <summary>空闲多久后发 PING（毫秒）。PRD 原值 20 s。</summary>
    public const int IdlePingMs = 20000;

    /// <summary>PING 发出后等多久判死（毫秒）。</summary>
    public const int PongTimeoutMs = 10000;

    private long _lastTrafficMs;
    private long _pingSentMs;
    private bool _pingOutstanding;

    /// <param name="nowMs">构造时刻的单调时钟，作为第一次计时起点。</param>
    public KeepalivePolicy(long nowMs)
    {
        _lastTrafficMs = nowMs;
    }

    /// <summary>发生任何流量（收或发，含 PONG）时调用：计时归零、未决 PING 清掉。</summary>
    public void OnTraffic(long nowMs)
    {
        _lastTrafficMs = nowMs;
        _pingOutstanding = false;
    }

    /// <summary>发完 PING 后调用。与 <see cref="OnTraffic"/> 分开是因为发 PING 本身**不算流量**——它不该把自己的计时清掉。</summary>
    public void OnPingSent(long nowMs)
    {
        _pingSentMs = nowMs;
        _pingOutstanding = true;
    }

    /// <summary>评估当前该做什么。泵每轮循环调一次。</summary>
    public KeepaliveAction Evaluate(long nowMs)
    {
        if (_pingOutstanding)
        {
            return nowMs - _pingSentMs >= PongTimeoutMs ? KeepaliveAction.DeclareDead : KeepaliveAction.None;
        }

        return nowMs - _lastTrafficMs >= IdlePingMs ? KeepaliveAction.SendPing : KeepaliveAction.None;
    }
}
