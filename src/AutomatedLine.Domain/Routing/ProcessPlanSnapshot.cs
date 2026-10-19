using System.Security.Cryptography;
using System.Text;

namespace AutomatedLine.Domain.Routing;

/// <summary>工件首次启动时锁定的不可变工艺路线；后续设备状态和模板变更均不能删减必做工序。</summary>
public sealed class ProcessPlanSnapshot
{
    /// <summary>锁定的有序工序，包含固定上料和下料节点。</summary>
    public IReadOnlyList<ProcessNode> Nodes { get; }

    /// <summary>由完整路线计算的版本标识，相同路线具有相同版本。</summary>
    public string Version { get; }

    /// <summary>用于任务和操作日志的完整路线。</summary>
    public string Description => string.Join(" → ", Nodes.Select(node => node.Process));

    /// <summary>复制并校验路线，防止调用方修改原集合影响已启动工件。</summary>
    /// <param name="nodes">包含上料起点、下料终点且工序不重复的有序节点。</param>
    public ProcessPlanSnapshot(IEnumerable<ProcessNode> nodes)
    {
        var copy = nodes.Select(node => new ProcessNode { Process = node.Process, DeviceType = node.DeviceType }).ToArray();
        if (copy.Length < 2 || copy[0].Process != ProcessType.Loading || copy[^1].Process != ProcessType.Unloading
            || copy.Select(node => node.Process).Distinct().Count() != copy.Length)
            throw new ArgumentException("工艺必须包含固定上料、下料且工序不能重复", nameof(nodes));
        Nodes = Array.AsReadOnly(copy);
        var key = string.Join(",", copy.Select(node => $"{(int)node.Process}:{(int)node.DeviceType}"));
        Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }

    /// <summary>只沿锁定路线找下一工序；仅到下料终点才返回空，非法当前工序不能被误判完成。</summary>
    /// <param name="current">当前工序，Unknown按尚在上料队列处理。</param>
    /// <returns>下一工序；已经到下料终点返回null。</returns>
    public ProcessNode? GetNext(ProcessType current)
    {
        if (current == ProcessType.Unknown) current = ProcessType.Loading;
        for (var i = 0; i < Nodes.Count; i++)
            if (Nodes[i].Process == current) return i + 1 < Nodes.Count ? Nodes[i + 1] : null;
        throw new InvalidOperationException($"当前工序{current}不在已锁定工艺{Version}中");
    }
}
