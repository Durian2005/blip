using Lixin.Core.Protocol;

namespace Lixin.Tests;

/// <summary>
/// <see cref="Frame"/> 的相等性语义测试（对应审查 M2）。
///
/// record struct 的默认相等性对 <see cref="ReadOnlyMemory{T}"/> 字段是**按引用**比较的：
/// 两段内容完全相同的载荷，只要不是同一块数组，就判为不相等。
/// 对协议帧这种「线格式的值对象」来说这是错的语义——测试与诊断都要求**按内容**比较。
/// </summary>
public class FrameEqualityTests
{
    private static Frame Make(byte[] payload, byte[]? tag = null, uint sequence = 1,
        FrameType type = FrameType.Text, FrameFlags flags = FrameFlags.LastFragment)
        => new(ProtocolConstants.Version, type, flags, sequence, payload, tag ?? ReadOnlyMemory<byte>.Empty);

    /// <summary>核心断言：内容相同的两个帧必须相等，哪怕底层是不同的数组。</summary>
    [Fact]
    public void SameContent_DifferentArrays_AreEqual()
    {
        var a = Make("蓝牙离线通信"u8.ToArray());
        var b = Make("蓝牙离线通信"u8.ToArray());

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>相等性必须与 HashCode 一致，否则 HashSet/Dictionary 会判出两个相等的键。</summary>
    [Fact]
    public void EqualFrames_CollapseInHashSet()
    {
        var set = new HashSet<Frame> { Make("same"u8.ToArray()), Make("same"u8.ToArray()) };
        Assert.Single(set);
    }

    [Fact]
    public void DifferentPayload_IsNotEqual()
    {
        var a = Make("abc"u8.ToArray());
        var b = Make("abd"u8.ToArray());
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void DifferentLength_IsNotEqual()
    {
        var a = Make("abc"u8.ToArray());
        var b = Make("abcd"u8.ToArray());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentSequence_IsNotEqual()
    {
        Assert.NotEqual(Make("abc"u8.ToArray(), sequence: 1), Make("abc"u8.ToArray(), sequence: 2));
    }

    [Fact]
    public void DifferentTypeOrFlags_IsNotEqual()
    {
        var a = Make("abc"u8.ToArray(), type: FrameType.Text);
        var b = Make("abc"u8.ToArray(), type: FrameType.Ping);
        Assert.NotEqual(a, b);

        var c = Make("abc"u8.ToArray(), flags: FrameFlags.None);
        Assert.NotEqual(Make("abc"u8.ToArray(), flags: FrameFlags.LastFragment), c);
    }

    [Fact]
    public void DifferentAuthTag_IsNotEqual()
    {
        var a = Make("abc"u8.ToArray(), tag: new byte[ProtocolConstants.AuthTagSize]);
        var b = Make("abc"u8.ToArray(), tag: Enumerable.Repeat((byte)0xFF, ProtocolConstants.AuthTagSize).ToArray());
        Assert.NotEqual(a, b);
    }

    /// <summary>反过来说：都没有 tag 的帧，不能因为「都是空」以外的原因相等。</summary>
    [Fact]
    public void EmptyTagFrames_CompareByPayloadAlone()
    {
        Assert.Equal(Make("x"u8.ToArray()), Make("x"u8.ToArray()));
    }

    /// <summary>Equals(object) 重载必须与强类型版本行为一致。</summary>
    [Fact]
    public void EqualsObject_MatchesTypedEquals()
    {
        var a = Make("obj"u8.ToArray());
        object boxed = Make("obj"u8.ToArray());
        Assert.True(a.Equals(boxed));
        Assert.False(a.Equals("not a frame"));
        Assert.False(a.Equals(null));
    }

    /// <summary>ToString 是排障时唯一会看的东西，必须能一眼认出帧的类型与规模。</summary>
    [Fact]
    public void ToString_CarriesTypeAndSizes()
    {
        var text = Make(new byte[300]);
        var s = text.ToString();
        Assert.Contains(nameof(FrameType.Text), s, StringComparison.Ordinal);
        Assert.Contains("300", s, StringComparison.Ordinal);

        var withTag = Make("ab"u8.ToArray(), tag: new byte[ProtocolConstants.AuthTagSize]);
        Assert.Contains("16", withTag.ToString(), StringComparison.Ordinal);
    }
}
