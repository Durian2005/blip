namespace Lixin.Core.Protocol;

/// <summary>协议常量。改动任何一项都会破坏线格式兼容性，必须同步改 PRD §7.5。</summary>
public static class ProtocolConstants
{
    /// <summary>帧起始魔数，ASCII "LC"。</summary>
    public const ushort Magic = 0x4C43;

    /// <summary>协议版本。</summary>
    public const byte Version = 1;

    /// <summary>
    /// 帧头长度：MAGIC(2) + VER(1) + TYPE(1) + FLAGS(1) + SEQ(4) + LEN(2) + TAGLEN(1)。
    /// </summary>
    public const int HeaderSize = 12;

    /// <summary>单帧载荷上限（RFCOMM/BLE 都友好的值）。</summary>
    public const int MaxPayload = 512;

    /// <summary>AEAD 认证标签长度。P2 阶段为 0（未加密），P4 起为 16。</summary>
    public const int AuthTagSize = 16;

    /// <summary>单条逻辑消息上限（32 KB，约 1.09 万汉字）。</summary>
    public const int MaxMessageSize = 32 * 1024;
}
