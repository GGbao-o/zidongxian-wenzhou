using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Application.Collection;

/// <summary>天车共享状态的只读入口，消费者不能借查询触发通信或改变任务运行态。</summary>
public interface ICraneStateProvider
{
    /// <summary>取得当前驱动最近一次采集结果，包含失败结果；仅供查看采集时间和诊断。</summary>
    /// <param name="craneId">天车标识。</param>
    /// <returns>最近结果；尚未采集、停用或驱动已更换时返回 null。</returns>
    CraneStateSnapshot? GetSnapshot(int craneId);

    /// <summary>取得有效且未过期的 PLC 状态，统一执行展示与调度的数据时效规则。</summary>
    /// <param name="craneId">天车标识。</param>
    /// <returns>可用状态；未采集、失败、过期、停用或驱动已更换时返回 null。</returns>
    /*   使用位置	                            用途大屏
         大屏展示页面 ScreenViewModel	        取各台天车状态，更新卡片
         手动页 ManualOperationViewModel	        取当前所选天车状态，更新左侧显示
         调度 CraneScheduler	                取启用且空闲天车的状态，用于选车
         冷启动 ColdStartChecker	                等待有效快照，核对有板情况*/
    CraneStatusDto? GetFreshStatus(int craneId);
}
