using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Routing;

/// <summary>
/// 工艺路线节点：一个工序 + 它对应的设备类型。
/// **是否参与工艺不由节点自己声明**，而是由"该设备类型当前有没有启用设备"决定
/// （见 <see cref="ProcessRoute.GetNextAvailable"/>）：没有设备的工序自动跳过，有设备的工序必经。
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
