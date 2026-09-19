using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 驱动注册表：保存设备驱动与天车驱动。
/// 设备号与天车号是**两个独立的命名空间**（例如设备1与天车1可以同时存在），因此分两个字典保存，
/// 绝不能用同一个键混存，否则会互相覆盖。
/// 采集器每周期从 <see cref="Snapshot"/> 取全部驱动读快照。
/// </summary>
public sealed class DriverRegistry
{
    /// <summary>设备号 → 设备驱动。</summary>
    private readonly Dictionary<int, IDeviceDriver> _deviceDrivers = new();

    /// <summary>天车号 → 天车驱动。</summary>
    private readonly Dictionary<int, ICraneDriver> _craneDrivers = new();

    /// <summary>保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>清空注册表。配置变更后重新装载前调用，避免残留旧驱动。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _deviceDrivers.Clear();
            _craneDrivers.Clear();
        }
    }

    /// <summary>注册设备驱动。</summary>
    /// <param name="driver">设备驱动</param>
    public void RegisterDevice(IDeviceDriver driver)
    {
        lock (_sync)
        {
            _deviceDrivers[driver.DeviceId] = driver;
        }
    }

    /// <summary>注册天车驱动。</summary>
    /// <param name="driver">天车驱动</param>
    public void RegisterCrane(ICraneDriver driver)
    {
        lock (_sync)
        {
            _craneDrivers[driver.CraneId] = driver;
        }
    }

    /// <summary>按设备号获取设备驱动。</summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>驱动；不存在返回 null</returns>
    public IDeviceDriver? GetDevice(int deviceId)
    {
        lock (_sync)
        {
            return _deviceDrivers.TryGetValue(deviceId, out var driver) ? driver : null;
        }
    }

    /// <summary>按天车号获取天车驱动。</summary>
    /// <param name="craneId">天车号</param>
    /// <returns>驱动；不存在返回 null</returns>
    public ICraneDriver? GetCrane(int craneId)
    {
        lock (_sync)
        {
            return _craneDrivers.TryGetValue(craneId, out var driver) ? driver : null;
        }
    }

    /// <summary>获取全部驱动（设备 + 天车）的快照，供采集器遍历。</summary>
    /// <returns>驱动列表快照</returns>
    public IReadOnlyList<IPlcDriver> Snapshot()
    {
        lock (_sync)
        {
            var all = new List<IPlcDriver>(_deviceDrivers.Count + _craneDrivers.Count);
            all.AddRange(_deviceDrivers.Values);
            all.AddRange(_craneDrivers.Values);
            return all;
        }
    }
}
