using Lixin.Transport;

namespace Lixin.Tests;

/// <summary>
/// 环境探针（T3.4）的环境无关契约。
/// 不断言具体探测结果——CI（无蓝牙）与本机（有蓝牙）必然不同——
/// 只钉住「探针绝不抛出、报告结构完整」这两个契约。
/// </summary>
public class BleEnvironmentProbeTests
{
    [Fact]
    public async Task RunAsync_任何环境都不抛出且报告结构完整()
    {
        var report = await BleEnvironmentProbe.RunAsync();

        Assert.NotNull(report);
        Assert.Equal(5, report.Items.Count);
        Assert.Equal(
            new[] { "adapter", "ble", "radio", "peripheralRole", "centralRole" },
            report.Items.Select(item => item.Id).ToArray());
        // 无论环境如何，结论只能是三态之一
        Assert.All(
            report.Items,
            item => Assert.True(
                item.Status is Lixin.Core.Env.BleCheckStatus.Ready
                    or Lixin.Core.Env.BleCheckStatus.NotReady
                    or Lixin.Core.Env.BleCheckStatus.Unknown));
    }

    [Fact]
    public void SettingsUri_默认为null且可初始化()
    {
        var plain = new BleGattStartupException(BleGattFailureReason.AdapterMissing, "没有适配器。");
        Assert.Null(plain.SettingsUri);

        var radioOff = new BleGattStartupException(BleGattFailureReason.BluetoothOff, "蓝牙未开启。")
        {
            SettingsUri = "ms-settings:bluetooth",
        };
        Assert.Equal("ms-settings:bluetooth", radioOff.SettingsUri);
        // 设置页 URI 与能力档案无关：开关关是临时状态
        Assert.False(radioOff.ShouldPersistCapabilityFailure);
    }
}
