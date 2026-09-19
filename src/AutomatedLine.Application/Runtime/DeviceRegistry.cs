using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 全线唯一设备对象目录（架构文档 §7）。
/// 保证同一个设备号永远返回同一个实例，杜绝"UI 显示一个、任务生成器又 new 一个"导致的状态不同步。
/// 线程安全：设备集合在启动时装载、运行期只读，用字典 + 锁保护初始化与读取。
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>设备号 → 设备对象。</summary>
    private readonly Dictionary<int, DeviceBase> _devices = new();

    /// <summary>保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 注册一台设备。重复的 Id 会被覆盖并给出提示（配置错误应尽早暴露）。
    /// </summary>
    /// <param name="device">设备对象</param>
    public void Register(DeviceBase device)
    {
        lock (_sync)
        {
            if (_devices.ContainsKey(device.Id))
            {
                Console.WriteLine($"[DeviceRegistry] 设备号 {device.Id} 重复注册，已覆盖");
            }

            _devices[device.Id] = device;
        }
    }

    /// <summary>
    /// 按设备号获取设备对象。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>设备对象；不存在返回 null</returns>
    public DeviceBase? Get(int deviceId)
    {
        lock (_sync)
        {
            return _devices.TryGetValue(deviceId, out var device) ? device : null;
        }
    }

    /// <summary>
    /// 获取全部设备的快照。
    /// </summary>
    /// <returns>设备列表快照</returns>
    public IReadOnlyList<DeviceBase> Snapshot()
    {
        lock (_sync)
        {
            return _devices.Values.ToList();
        }
    }
}
