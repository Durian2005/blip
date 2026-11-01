using Lixin.Transport;

namespace Lixin.Tests;

/// <summary>
/// 外设通道里**不依赖蓝牙硬件**的那部分语义。
///
/// <see cref="BleGattPeripheralChannel.StartAsync"/> 本身必须有真机才能跑，
/// 但它的参数默认值与失败分类是纯逻辑，而且后者直接决定 T3.4 会怎么提示用户、
/// 以及会不会把本机永久标记成「不能当外设」——这类错误一旦上线就极难回滚，
/// 所以用测试把语义钉住。
/// </summary>
public class BleGattPeripheralChannelTests
{
    [Fact]
    public void Options_Defaults_AreSafe()
    {
        var options = new BleGattPeripheralOptions();

        Assert.Equal(BleGattInbox.DefaultCapacity, options.InboxCapacity);
        Assert.True(options.AlwaysRespondToWrite);
        Assert.True(options.MetaPayload.IsEmpty);
        Assert.True(options.ServiceData.IsEmpty);
        Assert.Null(options.Log);
    }

    [Fact]
    public void Options_AreImmutableViaWith()
    {
        // record 的 with 语义：改一个字段不能影响原对象（配置会被多处共享）
        var original = new BleGattPeripheralOptions { InboxCapacity = 8 };
        var changed = original with { InboxCapacity = 16 };

        Assert.Equal(8, original.InboxCapacity);
        Assert.Equal(16, changed.InboxCapacity);
    }

    /// <summary>
    /// 只有「本机真的不支持外设角色」才写能力档案。
    /// </summary>
    /// <remarks>
    /// 这条是 T3.2 踩过的坑的守卫：若把 BluetoothOff / AdvertisingFailed 也算进去，
    /// 一次忘了开蓝牙就会让本机永久放弃外设资格，之后永远只当中心，且没有任何提示。
    /// </remarks>
    [Theory]
    [InlineData(BleGattFailureReason.PeripheralRoleUnsupported, true)]
    [InlineData(BleGattFailureReason.AdapterMissing, false)]
    [InlineData(BleGattFailureReason.BluetoothOff, false)]
    [InlineData(BleGattFailureReason.ServiceRegistrationFailed, false)]
    [InlineData(BleGattFailureReason.AdvertisingFailed, false)]
    public void ShouldPersistCapabilityFailure_OnlyForMissingPeripheralRole(
        BleGattFailureReason reason,
        bool expected)
    {
        var exception = new BleGattStartupException(reason, "测试用消息");

        Assert.Equal(expected, exception.ShouldPersistCapabilityFailure);
        Assert.Equal(reason, exception.Reason);
        Assert.Equal("测试用消息", exception.Message);
    }
}
