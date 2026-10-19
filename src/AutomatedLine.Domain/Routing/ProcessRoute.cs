using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 默认工序顺序及设备类型目录。应用层结合工序勾选和启用设备构建新工件路线，
/// 首次启动后由ProcessPlanSnapshot固定，不能再用本目录的设备过滤方法改写老工件路线。
/// </summary>
public sealed class ProcessRoute
{
    /// <summary>按顺序排列的工艺节点。</summary>
    private readonly List<ProcessNode> _nodes;

    /// <summary>
    /// 只读的节点序列。
    /// </summary>
    public IReadOnlyList<ProcessNode> Nodes => _nodes;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="nodes">按工艺顺序排列的节点</param>
    public ProcessRoute(IEnumerable<ProcessNode> nodes)
    {
        _nodes = nodes.ToList();
    }

    /// <summary>
    /// 创建默认工艺路线（上料 → 双头镗 → 斜床 → 研磨 → 动平衡 → 打号 → 验点 → 下料）。
    /// 此处只定义先后顺序；新工件实际参与项由应用层工艺模板确定并在首次启动冻结。
    /// </summary>
    /// <returns>默认工艺路线</returns>
    public static ProcessRoute CreateDefault() => new(new[]
    {
        new ProcessNode { Process = ProcessType.Loading,   DeviceType = DeviceType.LoadRack },
        new ProcessNode { Process = ProcessType.Boring,    DeviceType = DeviceType.BoringMachine },
        new ProcessNode { Process = ProcessType.Lathing,   DeviceType = DeviceType.LatheMachine },
        new ProcessNode { Process = ProcessType.Grinding,  DeviceType = DeviceType.GrinderMachine },
        new ProcessNode { Process = ProcessType.Balancing, DeviceType = DeviceType.BalancingMachine },
        new ProcessNode { Process = ProcessType.Marking,   DeviceType = DeviceType.MarkerMachine },
        new ProcessNode { Process = ProcessType.Checking,  DeviceType = DeviceType.CheckMachine },
        new ProcessNode { Process = ProcessType.Unloading, DeviceType = DeviceType.UnloadRack }
    });

    /// <summary>
    /// 构造某设备类型当前是否有启用设备的判据，仅用于新工件模板和设备展示。
    /// 已启动工件沿自身快照取下一工序，不得再次按此判据跳过必做工序。
    /// </summary>
    /// <param name="devices">设备注册表快照</param>
    /// <returns>判据：该类型有启用设备返回 true</returns>
    public static Func<DeviceType, bool> AvailableDeviceFilter(IReadOnlyList<DeviceBase> devices)
        => type => devices.Any(d => d.Type == type && d.IsEnabled);

    /// <summary>
    /// 计算指定工序的下一道工序（架构文档 §6.5：现算，不持久化）。
    /// </summary>
    /// <param name="current">当前工序</param>
    /// <returns>下一道工序节点；已是最后一道或未找到时返回 null</returns>
    public ProcessNode? GetNext(ProcessType current)
    {
        var index = _nodes.FindIndex(n => n.Process == current);
        if (index < 0 || index >= _nodes.Count - 1)
        {
            return null;
        }

        return _nodes[index + 1];
    }

    /// <summary>
    /// 在默认目录中计算下一道具有设备的工序，保留用于目录预览；不用于已启动工件调度。
    /// </summary>
    /// <param name="current">当前工序</param>
    /// <param name="hasDevice">判断某设备类型当前是否有启用设备</param>
    /// <returns>下一道可执行工序；无则返回 null</returns>
    public ProcessNode? GetNextAvailable(ProcessType current, Func<DeviceType, bool> hasDevice)
    {
        var index = _nodes.FindIndex(n => n.Process == current);
        if (index < 0)
        {
            return null;
        }

        for (var i = index + 1; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (hasDevice(node.DeviceType))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// 判断某工序是否已是最后一道。
    /// </summary>
    /// <param name="current">当前工序</param>
    /// <returns>是最后一道返回 true</returns>
    public bool IsLast(ProcessType current)
    {
        var last = _nodes.Count > 0 ? _nodes[^1] : null;
        return last is not null && last.Process == current;
    }
}
