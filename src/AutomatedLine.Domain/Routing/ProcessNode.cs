using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 工艺路线节点：一个工序 + 它对应的设备类型。
/// 是否参与由首次启动捕获的工艺快照决定；节点一旦进入工件快照便为必经工序。
/// </summary>
public sealed class ProcessNode
{
    /// <summary>工序。</summary>
    public ProcessType Process { get; init; }

    /// <summary>该工序对应的设备类型。用于把工件路由到具体设备实例。</summary>
    public DeviceType DeviceType { get; init; }

    /// <summary>
    /// 便于日志/界面显示。
    /// </summary>
    /// <returns>工序文本</returns>
    public override string ToString() => Process.ToString();
}
