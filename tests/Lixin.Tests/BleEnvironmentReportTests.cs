using Lixin.Core.Env;

namespace Lixin.Tests;

/// <summary>
/// 环境自检报告（T3.4）的纯逻辑规则。
/// 阻断语义刻意与通道启动一致：Radio 查不到不阻断、角色不支持不阻断（D1 角色自适应）。
/// </summary>
public class BleEnvironmentReportTests
{
    private static BleEnvironmentReport Build(
        bool adapterPresent = true,
        bool? bleCapable = true,
        bool radioKnown = true,
        bool radioOn = true,
        bool? peripheralRole = true,
        bool? centralRole = true)
        => BleEnvironmentReport.Build(adapterPresent, bleCapable, radioKnown, radioOn, peripheralRole, centralRole);

    [Fact]
    public void Build_全部正常_五项全Ready且IsReady()
    {
        var report = Build();

        Assert.True(report.IsReady);
        Assert.Null(report.SettingsUri);
        Assert.Null(report.FirstBlockerHint);
        Assert.Equal(5, report.Items.Count);
        Assert.All(report.Items, item => Assert.Equal(BleCheckStatus.Ready, item.Status));
        Assert.Equal(
            new[] { "adapter", "ble", "radio", "peripheralRole", "centralRole" },
            report.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Build_蓝牙开关关_不Ready且带设置页Uri()
    {
        var report = Build(radioOn: false);

        Assert.False(report.IsReady);
        Assert.Equal("ms-settings:bluetooth", report.SettingsUri);
        Assert.Contains("ms-settings:bluetooth", report.FirstBlockerHint);
        Assert.Contains("蓝牙", report.FirstBlockerHint);

        var radio = report.Items.Single(item => item.Id == "radio");
        Assert.Equal(BleCheckStatus.NotReady, radio.Status);
        Assert.NotNull(radio.FixHint);
    }

    [Fact]
    public void Build_适配器缺失_其余项Unknown且无设置页Uri()
    {
        var report = Build(
            adapterPresent: false, bleCapable: null, radioKnown: false,
            peripheralRole: null, centralRole: null);

        Assert.False(report.IsReady);
        // 没有适配器就谈不上"开蓝牙"，设置页 URI 反而误导
        Assert.Null(report.SettingsUri);
        Assert.Contains("适配器", report.FirstBlockerHint);
        Assert.Contains("USB", report.FirstBlockerHint);

        Assert.All(
            report.Items.Where(item => item.Id != "adapter"),
            item => Assert.Equal(BleCheckStatus.Unknown, item.Status));
    }

    [Fact]
    public void Build_Radio查询失败_不阻断IsReady()
    {
        // 与 EnsureRadioOnAsync 的「查询失败继续尝试」同一语义：查不到 ≠ 开关关着
        var report = Build(radioKnown: false);

        Assert.True(report.IsReady);
        Assert.Null(report.SettingsUri);
        Assert.Null(report.FirstBlockerHint);

        var radio = report.Items.Single(item => item.Id == "radio");
        Assert.Equal(BleCheckStatus.Unknown, radio.Status);
        Assert.NotNull(radio.Detail);
    }

    [Fact]
    public void Build_不支持BLE_阻断但无设置页Uri()
    {
        var report = Build(bleCapable: false);

        Assert.False(report.IsReady);
        // 设置页解决不了硬件能力缺失
        Assert.Null(report.SettingsUri);
        Assert.Contains("BLE", report.FirstBlockerHint);
    }

    [Fact]
    public void Build_角色不支持_信息项不阻断IsReady()
    {
        var report = Build(peripheralRole: false, centralRole: false);

        // D1 角色自适应本来就是为了能力不对等：不支持角色是常态，不是错误
        Assert.True(report.IsReady);
        Assert.Null(report.SettingsUri);

        foreach (var role in report.Items.Where(item => item.Id.EndsWith("Role")))
        {
            Assert.Equal(BleCheckStatus.NotReady, role.Status);
            Assert.NotNull(role.FixHint);
        }
    }

    [Fact]
    public void Build_多个阻断项并存_提示取第一个阻断项()
    {
        // 检查顺序：适配器 → BLE → 开关。适配器缺失排最前，提示必须是适配器的
        var report = Build(adapterPresent: false, radioOn: false);

        Assert.Contains("适配器", report.FirstBlockerHint);
    }
}
