using System.Security.Cryptography;

namespace Lixin.Core.Election;

/// <summary>
/// 竞选键：本机持久化的 16 字节随机 ID，角色选举的唯一依据（PRD §7.4.1 规则 2）。
/// 字典序小者当外设、大者当中心——用确定性比较而非「先到先得」，
/// 才能保证双方各自独立决策时得到**相反且互补**的角色，不会同时连或同时等。
/// </summary>
public readonly record struct CampaignKey
{
    /// <summary>键的字节长度。固定 16 字节（128 位），无歧义且广播放得下。</summary>
    public const int Size = 16;

    private CampaignKey(ReadOnlyMemory<byte> value)
    {
        if (value.Length != Size)
        {
            throw new ArgumentException($"竞选键必须恰好 {Size} 字节，实际 {value.Length}。", nameof(value));
        }

        Value = value;
    }

    /// <summary>键值。只读视图，禁止外部修改。</summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>生成一个加密安全的随机竞选键。</summary>
    public static CampaignKey NewRandom()
    {
        var bytes = new byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new CampaignKey(bytes);
    }

    /// <summary>从字节构造（加载持久化身份时用）。长度不符直接抛错。</summary>
    public static CampaignKey FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    /// <summary>字典序比较：负数表示本键更小（当外设），正数表示更大（当中心），0 表示完全相同。</summary>
    public int CompareTo(CampaignKey other) => Value.Span.SequenceCompareTo(other.Value.Span);

    public static bool operator <(CampaignKey left, CampaignKey right) => left.CompareTo(right) < 0;
    public static bool operator >(CampaignKey left, CampaignKey right) => left.CompareTo(right) > 0;

    public override string ToString()
    {
        var hex = Convert.ToHexString(Value.Span);
        return $"CampaignKey({hex[..8]}…{hex[^4..]})";
    }
}
