using Lixin.Core.Env;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace Lixin.Transport;

/// <summary>
/// 环境自检探针（T3.4）：用 WinRT 实际探测本机蓝牙环境，
/// 把原始结果交给纯逻辑的 <see cref="BleEnvironmentReport.Build"/> 出报告。
/// 本类不做任何判断——所有决策逻辑都在 Core 的报告模型里，可单测。
/// </summary>
/// <remarks>
/// 每一步探测都可能失败（未打包进程无权查 Radio、虚拟机没有适配器），
/// 失败一律降级为 null / radioKnown=false，<b>绝不抛出</b>——
/// 自检的定位是「告诉用户现在能不能跑、不能跑怎么办」，自己先崩就本末倒置了。
/// </remarks>
public static class BleEnvironmentProbe
{
    /// <summary>探测本机蓝牙环境。任何异常都会被吸收成 Unknown 项，不抛出。</summary>
    public static async Task<BleEnvironmentReport> RunAsync(CancellationToken cancellationToken = default)
    {
        bool adapterPresent = false;
        bool? bleCapable = null;
        bool radioKnown = false;
        bool radioOn = false;
        bool? peripheralRole = null;
        bool? centralRole = null;

        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (adapter is not null)
            {
                adapterPresent = true;
                bleCapable = adapter.IsLowEnergySupported;
                peripheralRole = adapter.IsPeripheralRoleSupported;
                centralRole = adapter.IsCentralRoleSupported;

                // Radio 查询单独包 try：它是最容易因权限失败的一步，
                // 不能因为查不到开关状态就把「适配器存在/支持 BLE」这些已到手的信息一起丢掉。
                try
                {
                    var radio = await adapter.GetRadioAsync()
                        .AsTask(cancellationToken).ConfigureAwait(false);
                    radioKnown = true;
                    radioOn = radio.State == RadioState.On;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // radioKnown 保持 false：报告项显示「查询失败，不阻断」
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 适配器查询本身失败（虚拟机、驱动异常）：按「无适配器」出报告
        }

        return BleEnvironmentReport.Build(
            adapterPresent, bleCapable, radioKnown, radioOn, peripheralRole, centralRole);
    }
}
