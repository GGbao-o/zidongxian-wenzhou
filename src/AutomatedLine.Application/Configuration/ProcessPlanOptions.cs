using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Application.Configuration;

/// <summary>新工件的工艺模板开关；缺项默认启用，有设备的工序才纳入新路线，上下料固定保留。</summary>
public sealed class ProcessPlanOptions
{
    /// <summary>按工序名保存是否启用；省略项视为true，不能取消固定上下料节点。</summary>
    public Dictionary<ProcessType, bool> EnabledProcesses { get; set; } = new();
}
