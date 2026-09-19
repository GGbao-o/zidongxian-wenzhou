using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Infrastructure.Persistence.Entities;

/// <summary>
/// 工艺路线配置实体（映射 ProcessRouteConfig 表）。
/// 每一行代表工艺链上的一个节点：工序顺序 + 对应设备类型 + 是否可选。
/// v1 可先内置默认顺序，后续改由本表读取（架构文档 §6.5、§14.2）。
/// </summary>
public class ProcessRouteConfig
{
    /// <summary>主键。</summary>
    public int Id { get; set; }

    /// <summary>节点在工艺链中的顺序（从 0 或 1 开始由现场约定，越小越靠前）。</summary>
    public int StepOrder { get; set; }

    /// <summary>该节点对应的工序。</summary>
    public ProcessType Process { get; set; } = ProcessType.Unknown;

    /// <summary>该工序对应的设备类型。用于把工件路由到具体设备实例。</summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;

    /// <summary>是否为可选工序。可选节点在缺少对应设备时允许跳过。</summary>
    public bool IsOptional { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
