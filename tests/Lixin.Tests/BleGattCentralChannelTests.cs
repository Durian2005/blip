using Lixin.Transport;

namespace Lixin.Tests;

/// <summary>
/// 中心通道里**不依赖蓝牙硬件**的那部分语义。
///
/// <see cref="BleGattCentralChannel.StartAsync"/> 本身必须有真机（或手机外设）才能跑，
/// 但它的参数默认值与失败分类是纯逻辑——后者直接决定 T3.4 会怎么提示用户，
/// 用测试把语义钉住。重试参数与外设侧逐一对齐，两侧共享同一套退避语义。
/// </summary>
public class BleGattCentralChannelTests
{
    [Fact]
    public void Options_Defaults_AreSafe()
    {
        var options = new BleGattCentralOptions();

        Assert.Equal(BleGattInbox.DefaultCapacity, options.InboxCapacity);
        Assert.Null(options.Log);
        Assert.Null(options.TargetAddress);          // 默认走扫描
        Assert.True(options.ScanTimeoutMs > 0);
        Assert.True(options.ScanFreshnessMs > 0);
    }

    [Fact]
    public void Options_AreImmutableViaWith()
    {
        var original = new BleGattCentralOptions { InboxCapacity = 8 };
        var changed = original with { InboxCapacity = 16, TargetAddress = 0xAB };

        Assert.Equal(8, original.InboxCapacity);
        Assert.Null(original.TargetAddress);
        Assert.Equal(16, changed.InboxCapacity);
        Assert.Equal(0xABUL, changed.TargetAddress);
    }

    [Fact]
    public void RetryConstants_MatchPeripheralSide()
    {
        // 两侧共享同一套退避语义（design §10.1）：10 ms × 20 次 ≈ 200 ms。
        // 若单侧调整，另一侧必须同步评审——所以用测试把"相等"钉住。
        Assert.Equal(BleGattPeripheralChannel.NotifyRetryDelayMs, BleGattCentralChannel.WriteRetryDelayMs);
        Assert.Equal(BleGattPeripheralChannel.NotifyMaxRetries, BleGattCentralChannel.WriteMaxRetries);
    }

    /// <summary>
    /// 新增的中心侧失败原因**一律不写能力档案**。
    /// </summary>
    /// <remarks>
    /// 能力档案只记「本机根本做不到某个角色」。扫描超时/对端不在这类链路质量原因
    /// 绝不能入库，否则一次对方没开机就把本机永久标记成残废。
    /// </remarks>
    [Theory]
    [InlineData(BleGattFailureReason.CentralRoleUnsupported, false)]
    [InlineData(BleGattFailureReason.DiscoveryFailed, false)]
    [InlineData(BleGattFailureReason.ConnectionFailed, false)]
    [InlineData(BleGattFailureReason.ServiceDiscoveryFailed, false)]
    [InlineData(BleGattFailureReason.SubscribeFailed, false)]
    public void CentralFailureReasons_NeverPersistCapability(
        BleGattFailureReason reason,
        bool expectedPersist)
    {
        var exception = new BleGattStartupException(reason, "测试用消息");

        Assert.Equal(expectedPersist, exception.ShouldPersistCapabilityFailure);
        Assert.Equal(reason, exception.Reason);
    }
}
