using Lixin.Transport;

namespace Lixin.Tests;

public class BleGattUuidsTests
{
    [Fact]
    public void AllUuids_AreDistinct()
    {
        // 复制粘贴改 UUID 时最容易漏改一位，导致两个特征值撞成同一个。
        Assert.Equal(4, BleGattUuids.All.Distinct().Count());
    }

    [Fact]
    public void AllUuids_AreNotEmpty()
    {
        Assert.All(BleGattUuids.All, uuid => Assert.NotEqual(Guid.Empty, uuid));
    }

    [Fact]
    public void AllUuids_ShareLixinPrefix()
    {
        // 前缀是 "lixin" 的 ASCII，抓包时要靠它一眼认出自家流量。
        Assert.All(BleGattUuids.All, uuid => Assert.StartsWith("6c697869-6e", uuid.ToString("D")));
    }

    [Fact]
    public void AllUuids_AvoidSigReservedBaseSuffix()
    {
        // 00805f9b34fb 是 Bluetooth SIG 的保留基础后缀（官方 16/32 位 UUID 都靠它展开）。
        // 自定义 UUID 用它会让抓包工具误判成官方定义的服务。
        Assert.All(
            BleGattUuids.All,
            uuid => Assert.DoesNotContain("00805f9b34fb", uuid.ToString("D")));
    }

    [Fact]
    public void ServiceAndCharacteristics_AreWithinSameFamily()
    {
        // 末两位区分 4 个实体（…e60/e61/e62/e63），这是这份表的可读性约定：
        // 粘贴时把 Tx 和 Rx 搞反是很容易犯的错，末两位对不上会立刻暴露。
        // 注意 Guid.ToString("D") 的最后一段是 12 位十六进制、前面没有连字符，
        // 所以只能取末两位比较，不能断言 "-61" 之类的结尾。
        Assert.Equal("60", BleGattUuids.Service.ToString("D")[^2..]);
        Assert.Equal("61", BleGattUuids.Tx.ToString("D")[^2..]);
        Assert.Equal("62", BleGattUuids.Rx.ToString("D")[^2..]);
        Assert.Equal("63", BleGattUuids.Meta.ToString("D")[^2..]);
    }
}
