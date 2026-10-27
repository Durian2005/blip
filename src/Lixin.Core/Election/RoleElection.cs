namespace Lixin.Core.Election;

/// <summary>一方在 BLE 连接中可承担的能力组合（对应广播里的能力位）。</summary>
/// <param name="CanAdvertise">能否当外设：发布可连接广播、托管 GATT 服务、被连接。</param>
/// <param name="CanConnect">能否当中心：扫描、发起 GATT 连接。</param>
public readonly record struct RoleCapabilities(bool CanAdvertise, bool CanConnect)
{
    /// <summary>双模全能（现实中几乎所有适配器都是这个）。</summary>
    public static readonly RoleCapabilities Both = new(true, true);

    /// <summary>只能当外设（罕见）。</summary>
    public static readonly RoleCapabilities PeripheralOnly = new(true, false);

    /// <summary>只能当中心（罕见；外设角色启动失败写入能力档案后，本机在本次运行等效于此）。</summary>
    public static readonly RoleCapabilities CentralOnly = new(false, true);
}

/// <summary>选举结果。双方**各自独立**调用 <see cref="RoleElection.Decide"/>，结果必然互补。</summary>
public enum ElectionDecision
{
    /// <summary>本机当外设：保持可连接广播，等对方来连。</summary>
    BecomePeripheral,

    /// <summary>本机当中心：停止可连接广播（避免对端误判），主动扫描并连接。</summary>
    BecomeCentral,

    /// <summary>
    /// 双方竞选键完全相同（2^128 概率，仅防御性处理）。
    /// 此时双方得到**同一个**结果，无法互补——调用方应等待一段随机抖动后重读广播、重新选举。
    /// </summary>
    ConflictRetry,

    /// <summary>能力组合无法建立连接（例如双方都只能当中心：没人发布可连接广播）。UI 应明确报错。</summary>
    Impossible,
}

/// <summary>
/// 角色选举（PRD §7.4.1）。纯函数，无状态，双方输入对称，结果互补。
///
/// 设计要点：
/// 1. **确定性优于抢占**。「先到先得」会导致双方同时连（撞车）或同时等（死锁）；
///    竞选键字典序是双方独立计算也能得到一致结果的确定性规则。
/// 2. **能力位优先于竞选键**。一方不能当外设时，键再小也只能当中心——能力是硬约束。
/// 3. **角色不绑定**。会话结束后不保留上次角色，下次启动重新选举（避免一方永久沦为外设）。
/// </summary>
public static class RoleElection
{
    /// <summary>
    /// 决定本机角色。
    /// </summary>
    /// <param name="myKey">本机竞选键（持久化身份）。</param>
    /// <param name="mine">本机能力。</param>
    /// <param name="peerKey">对端竞选键（来自对端广播）。</param>
    /// <param name="peer">对端能力（来自对端广播的能力位）。</param>
    public static ElectionDecision Decide(
        CampaignKey myKey, RoleCapabilities mine,
        CampaignKey peerKey, RoleCapabilities peer)
    {
        // 没人能发布可连接广播 / 没人能发起连接，链路在拓扑上就不成立
        if (!mine.CanAdvertise && !peer.CanAdvertise) return ElectionDecision.Impossible;
        if (!mine.CanConnect && !peer.CanConnect) return ElectionDecision.Impossible;

        // 能力不对等时硬约束优先，竞选键不参与
        if (!mine.CanAdvertise) return ElectionDecision.BecomeCentral;   // 只能主动连
        if (!peer.CanAdvertise) return ElectionDecision.BecomePeripheral; // 对端连不了，只能被连
        if (!mine.CanConnect) return ElectionDecision.BecomePeripheral;   // 只能被连
        if (!peer.CanConnect) return ElectionDecision.BecomeCentral;      // 对端只能主动连

        // 双方全能：竞选键字典序，小者当外设
        var comparison = myKey.CompareTo(peerKey);
        if (comparison == 0) return ElectionDecision.ConflictRetry;

        return comparison < 0
            ? ElectionDecision.BecomePeripheral
            : ElectionDecision.BecomeCentral;
    }
}
