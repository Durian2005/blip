namespace Lixin.Core.Protocol;

/// <summary>分片重组的结果。</summary>
public enum AssemblyResult
{
    /// <summary>还没收全，继续等。</summary>
    NeedMore,

    /// <summary>收到最后一片，消息已完整。</summary>
    Completed,

    /// <summary>序号断裂或超限，本次消息已作废（已自动重置，可继续收下一条）。</summary>
    ProtocolError,

    /// <summary>
    /// 当前流处于「毒化」状态（上一条消息已被判定损坏），本帧被丢弃。
    /// 与 <see cref="ProtocolError"/> 的区别：后者是「刚刚出错」，前者是「出错的余波」。
    /// </summary>
    Discarded,
}

/// <summary>
/// 分片重组器：把帧流还原成完整消息。
///
/// 三条规则，都来自实测踩过的坑（见 docs/code-review-2026-10-26.md 的 C2 / C3）：
///
/// 1. **控制帧旁路**：<see cref="FrameTypes.IsFragmented"/> 为 false 的帧（Ping/Pong/Bye/Ack/...）
///    单帧自洽，直接交付，**完全不碰正在组装的数据消息**。
///    否则一条保活 Ping 会被当成正文的末片，把正文截成两半、各报一次「成功」。
///    这类帧也不占用数据帧的序号空间，因此不会在正文中间插出序号空洞。
///
/// 2. **毒化**：一旦判定消息损坏（序号断裂 / 超限 / 类型交错），后续帧**全部丢弃**，
///    直到看见某个数据消息的末片为止。若把残余分片当成「新消息的开头」继续组装，
///    就会把截断的内容当作完整消息交付，而上游毫无察觉——这比丢消息更难排查。
///
/// 3. **失聪有界**：因为每条完整消息都以末片结束，所以毒化**最多让下一条消息也被丢掉**，
///    再往后自动恢复正常。末片本身丢失也不会让链路永久静默。
///    （断线重连时调用 <see cref="Reset"/> 可立即清除。）
///
/// 限制（已知，非缺陷）：协议当前没有消息 ID，因此**同一方向同一时刻只允许一条数据消息在传**。
/// 若发送端把两条同类型数据消息交错发出，重组器无法分辨归属，需要发送端串行化来保证。
/// </summary>
public sealed class MessageAssembler
{
    private readonly MemoryStream _buffer = new();
    private uint _expectedSequence = uint.MaxValue;
    private FrameType _currentType;
    private bool _inMessage;
    private bool _poisoned;

    /// <summary>当前正在组装的消息已累积的字节数。</summary>
    public int CurrentSize => (int)_buffer.Length;

    /// <summary>是否处于「上一条消息已损坏，正在丢弃余波」的状态。</summary>
    public bool IsPoisoned => _poisoned;

    /// <summary>投递一个帧。</summary>
    public AssemblyResult Add(in Frame frame, out ReadOnlyMemory<byte> payload)
    {
        payload = ReadOnlyMemory<byte>.Empty;

        // ---------- 规则 1：控制帧旁路 ----------
        if (!FrameTypes.IsFragmented(frame.Type))
        {
            if (!frame.IsLastFragment)
            {
                // 控制帧被标成「非末片」是畸形输入；报错但不毒化，因为它不属于任何数据消息
                return AssemblyResult.ProtocolError;
            }

            payload = frame.Payload;
            return AssemblyResult.Completed;
        }

        // ---------- 规则 2：毒化期 ----------
        if (_poisoned)
        {
            // 末片意味着「某条消息到此为止」，流已回到消息边界，可以重新开始
            if (frame.IsLastFragment)
            {
                ClearState();
            }

            return AssemblyResult.Discarded;
        }

        // ---------- 规则 3：正常组装 ----------
        if (!_inMessage)
        {
            _inMessage = true;
            _currentType = frame.Type;
            _expectedSequence = frame.Sequence;
            _buffer.SetLength(0);
        }
        else if (frame.Type != _currentType)
        {
            // 两种数据消息交错，没有消息 ID 可分——作废，绝不能拼在一起
            return Fail(frame);
        }
        else if (frame.Sequence < _expectedSequence)
        {
            // 重复帧（重传或镜像），直接忽略，不算错误
            return AssemblyResult.NeedMore;
        }
        else if (frame.Sequence > _expectedSequence)
        {
            return Fail(frame);
        }

        // 单条消息长度上限是硬约束：超了说明序号已经错位，继续拼只会拼出垃圾
        if (_buffer.Length + frame.Payload.Length > ProtocolConstants.MaxMessageSize)
        {
            return Fail(frame);
        }

        _buffer.Write(frame.Payload.Span);
        _expectedSequence++;

        if (!frame.IsLastFragment)
        {
            return AssemblyResult.NeedMore;
        }

        payload = _buffer.ToArray();
        _inMessage = false;
        _buffer.SetLength(0);
        return AssemblyResult.Completed;
    }

    /// <summary>
    /// 全清：丢弃未完成的消息，并解除毒化状态。断线重连时调用（新连接是干净的信道）。
    /// </summary>
    public void Reset() => ClearState();

    /// <summary>
    /// 判定当前消息损坏。触发帧本身绝不交付（无法判断它属于新消息还是坏消息的尾巴）；
    /// 若触发帧是末片，说明流已回到消息边界，只需清状态；否则进入毒化，丢弃这条消息的残余。
    /// </summary>
    private AssemblyResult Fail(in Frame frame)
    {
        ClearState();

        if (!frame.IsLastFragment)
        {
            _poisoned = true;
        }

        return AssemblyResult.ProtocolError;
    }

    private void ClearState()
    {
        _buffer.SetLength(0);
        _inMessage = false;
        _poisoned = false;
        _expectedSequence = uint.MaxValue;
    }
}
