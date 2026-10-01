using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 工艺路线：固定的工序顺序。
/// 顺序**固定在内置代码中**，不进数据库。原因：车间排布一旦确定就不再调换设备，
/// 仅坐标可能微调，而坐标由 DeviceConfig.PositionX 管理（架构文档 §6.5）。
/// 下一工序现算，不持久化；若将来确需运行期调整顺序，再改为表驱动。
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
    /// 打号机、验点机是否上线以现场为准，此处标记为可选。
    /// </summary>
    /// <returns>默认工艺路线</returns>
    public static ProcessRoute CreateDefault() => new(new[]
    {
        new ProcessNode { Process = ProcessType.Loading,   DeviceType = DeviceType.LoadRack },
        new ProcessNode { Process = ProcessType.Boring,    DeviceType = DeviceType.BoringMachine },
        new ProcessNode { Process = ProcessType.Lathing,   DeviceType = DeviceType.LatheMachine },
        new ProcessNode { Process = ProcessType.Grinding,  DeviceType = DeviceType.GrinderMachine },
        new ProcessNode { Process = ProcessType.Balancing, DeviceType = DeviceType.BalancingMachine },
        new ProcessNode { Process = ProcessType.Marking,   DeviceType = DeviceType.MarkerMachine,  IsOptional = true },
        new ProcessNode { Process = ProcessType.Checking,  DeviceType = DeviceType.CheckMachine,   IsOptional = true },
        new ProcessNode { Process = ProcessType.Unloading, DeviceType = DeviceType.UnloadRack }
    });

    /// <summary>
    /// 获取第一个工艺节点。
    /// </summary>
    /// <returns>第一个节点；路线为空时返回 null</returns>
    public ProcessNode? GetFirst() => _nodes.Count > 0 ? _nodes[0] : null;

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
    /// 计算指定工序之后下一道**实际可执行**的工序：**跳过所有没有启用设备的工序节点**。
    /// 即"实际工序由启用设备决定"——某道工序（如动平衡/打号/验点）没有启用设备时，
    /// 工件直接进入再下一道，而不是卡死。返回 null 表示后面已无可执行工序（视为走完）。
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
