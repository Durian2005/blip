using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace Lixin.Transport;

/// <summary>
/// 外设/中心通道共用的环境检查。从外设侧原实现中提取，避免两份拷贝各自漂移。
/// </summary>
internal static class BleGattEnvironment
{
    /// <summary>
    /// 确认蓝牙开关已打开。
    /// </summary>
    /// <remarks>
    /// 未打包的桌面进程可能无权查询 Radio。这不该阻断启动：
    /// 真正的失败会在后续 GATT 操作里以明确的方式暴露出来。
    /// </remarks>
    public static async Task EnsureRadioOnAsync(
        BluetoothAdapter adapter,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            var radio = await adapter.GetRadioAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (radio.State != RadioState.On)
            {
                throw new BleGattStartupException(BleGattFailureReason.BluetoothOff,
                    "蓝牙未开启。请打开蓝牙开关后重试。")
                {
                    // T3.4：UI 层拿这个 URI 可以直接唤起系统蓝牙设置页
                    SettingsUri = "ms-settings:bluetooth",
                };
            }
        }
        catch (BleGattStartupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Radio 状态查询失败（不阻断，继续尝试）：{ex.GetType().Name}");
        }
    }
}
