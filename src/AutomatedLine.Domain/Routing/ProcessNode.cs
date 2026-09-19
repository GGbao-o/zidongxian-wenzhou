using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 工艺路线节点：一个工序 + 它对应的设备类型 + 是否可选。
/// </summary>
public sealed class ProcessNode
{
    /// <summary>工序。</summary>
    public ProcessType Process { get; init; }

    /// <summary>该工序对应的设备类型。用于把工件路由到具体设备实例。</summary>
    public DeviceType DeviceType { get; init; }

    /// <summary>是否可选。可选节点在缺少对应设备时允许跳过。</summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// 便于日志/界面显示。
    /// </summary>
    /// <returns>工序文本</returns>
    public override string ToString() => Process.ToString();
}
