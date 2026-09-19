using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 驱动注册表：保存"资源号 → 驱动实例"。
/// 驱动在配置装载时按协议创建（当前为模拟驱动），采集器每周期从这里取全部驱动读取快照。
/// </summary>
public sealed class DriverRegistry
{
    /// <summary>资源号 → 驱动。</summary>
    private readonly Dictionary<int, IPlcDriver> _drivers = new();

    /// <summary>保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 注册一个驱动。资源号来自设备/天车配置，重复注册会覆盖并提示。
    /// </summary>
    /// <param name="driver">驱动实例</param>
    public void Register(IPlcDriver driver)
    {
        var id = driver switch
        {
            IDeviceDriver deviceDriver => deviceDriver.DeviceId,
            ICraneDriver craneDriver => craneDriver.CraneId,
            _ => -1
        };

        if (id < 0)
        {
            Console.WriteLine("[DriverRegistry] 未知驱动类型，已忽略");
            return;
        }

        lock (_sync)
        {
            if (_drivers.ContainsKey(id))
            {
                Console.WriteLine($"[DriverRegistry] 资源号 {id} 驱动重复注册，已覆盖");
            }

            _drivers[id] = driver;
        }
    }

    /// <summary>
    /// 获取全部驱动的快照。
    /// </summary>
    /// <returns>驱动列表快照</returns>
    public IReadOnlyList<IPlcDriver> Snapshot()
    {
        lock (_sync)
        {
            return _drivers.Values.ToList();
        }
    }
}
