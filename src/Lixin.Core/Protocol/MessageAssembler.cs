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
}

/// <summary>
/// 分片重组器。同一时刻只组装一条消息；
/// 序号必须连续（BLE GATT 保证有序，出现断裂说明链路或对方实现有问题，直接作废本次）。
/// </summary>
public sealed class MessageAssembler
{
    private readonly MemoryStream _buffer = new();
    private uint _expectedSequence = uint.MaxValue;
    private bool _inMessage;

    /// <summary>当前正在组装的消息已累积的字节数。</summary>
    public int CurrentSize => (int)_buffer.Length;

    /// <summary>投递一个帧。</summary>
    public AssemblyResult Add(in Frame frame, out ReadOnlyMemory<byte> payload)
    {
        payload = ReadOnlyMemory<byte>.Empty;

        if (!_inMessage)
        {
            _inMessage = true;
            _expectedSequence = frame.Sequence;
            _buffer.SetLength(0);
        }
        else if (frame.Sequence < _expectedSequence)
        {
            // 重复帧（重传或镜像），直接忽略，不算错误
            return AssemblyResult.NeedMore;
        }
        else if (frame.Sequence > _expectedSequence)
        {
            // 序号断裂：本次消息作废
            Reset();
            return AssemblyResult.ProtocolError;
        }

        if (_buffer.Length + frame.Payload.Length > ProtocolConstants.MaxMessageSize)
        {
            Reset();
            return AssemblyResult.ProtocolError;
        }

        _buffer.Write(frame.Payload.Span);
        _expectedSequence++;

        if (!frame.IsLastFragment)
        {
            return AssemblyResult.NeedMore;
        }

        payload = _buffer.ToArray();
        Reset();
        return AssemblyResult.Completed;
    }

    /// <summary>丢弃当前未完成的消息（例如连接断开时调用）。</summary>
    public void Reset()
    {
        _buffer.SetLength(0);
        _inMessage = false;
        _expectedSequence = uint.MaxValue;
    }
}
