using AutomatedLine.Domain.Cranes;

namespace AutomatedLine.Application.Runtime;

/// <summary>
/// 全线唯一天车对象目录（架构文档 §7）。
/// 与 <see cref="DeviceRegistry"/> 同理：保证天车号到实例的一一对应。
/// </summary>
public sealed class CraneRegistry
{
    /// <summary>天车号 → 天车对象。</summary>
    private readonly Dictionary<int, Crane> _cranes = new();

    /// <summary>保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 清空注册表。配置在界面上增删改后重新装载前调用，避免残留旧实例。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _cranes.Clear();
        }
    }

    /// <summary>
    /// 注册一台天车。
    /// </summary>
    /// <param name="crane">天车对象</param>
    public void Register(Crane crane)
    {
        lock (_sync)
        {
            if (_cranes.ContainsKey(crane.Id))
            {
                Console.WriteLine($"[CraneRegistry] 天车号 {crane.Id} 重复注册，已覆盖");
            }

            _cranes[crane.Id] = crane;
        }
    }

    /// <summary>
    /// 按天车号获取天车对象。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <returns>天车对象；不存在返回 null</returns>
    public Crane? Get(int craneId)
    {
        lock (_sync)
        {
            return _cranes.TryGetValue(craneId, out var crane) ? crane : null;
        }
    }

    /// <summary>
    /// 获取全部天车的快照。
    /// </summary>
    /// <returns>天车列表快照</returns>
    public IReadOnlyList<Crane> Snapshot()
    {
        lock (_sync)
        {
            return _cranes.Values.ToList();
        }
    }
}
