using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Alarms;

/// <summary>
/// 异常任务放弃协调器（架构文档 §10.4）。
/// 异常只来自天车状态机执行动作；这里统一做**程序自动清理**：释放路权、取消源/目标预约、释放天车锁、
/// 清空天车与设备内存、丢弃该工件任务，并暂停线路。全程 try/catch/finally 保证一定释放。
/// 不要求操作员点击任何"清内存/解死锁"按钮。
/// </summary>
public sealed class TaskAbortCoordinator
{
    /// <summary>工件任务总表。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>路权管理器。</summary>
    private readonly RightOfWayManager _rightOfWayManager;

    /// <summary>预约管理器。</summary>
    private readonly ResourceReservationManager _reservationManager;

    /// <summary>线路控制。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>报警管理器。</summary>
    private readonly AlarmManager _alarmManager;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="taskManager">任务总表</param>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="rightOfWayManager">路权管理器</param>
    /// <param name="reservationManager">预约管理器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="alarmManager">报警管理器</param>
    /// <param name="logger">日志</param>
    public TaskAbortCoordinator(
        IRuntimeTaskManager taskManager,
        DeviceRegistry deviceRegistry,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        LineControlService lineControl,
        AlarmManager alarmManager,
        IAppLogger logger)
    {
        _taskManager = taskManager;
        _deviceRegistry = deviceRegistry;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _lineControl = lineControl;
        _alarmManager = alarmManager;
        _logger = logger;
    }

    /// <summary>
    /// 中止天车当前任务并按 §10.4 自动清理。
    /// </summary>
    /// <param name="crane">发生异常的天车</param>
    /// <param name="reason">异常原因（写报警） 这个参数是为了写日志弹窗的</param>
    public void Abort(Crane crane, string reason)
    {
        var task = crane.Runtime.CurrentTask;
        try
        {
            // 释放锁与预约（幂等，即使前面步骤失败也不影响）
            if (task is not null)
            {
                //路权释放
                _rightOfWayManager.Release(task.TaskId);
                //预约释放
                _reservationManager.Release(task.TaskId);
            }

            // 清空天车内存状态
            crane.Runtime.CurrentTask = null;
            crane.Runtime.State = CraneState.Idle;
            crane.Runtime.LockedInterval = null;
            crane.CurrentWorkpiece = null;

            if (task is not null)
            {
                // 清空相关设备内存引用（源/目标设备若正持有该工件）
                ClearDeviceWorkpiece(task.SourceDeviceId, task.WorkpieceRuntimeId);
                ClearDeviceWorkpiece(task.TargetDeviceId, task.WorkpieceRuntimeId);

                // 标记该工件为"已丢弃"（**保留记录**便于追溯；设备/天车引用已清、位置置 Discarded），不删除任务
                var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId);
                if (workpieceTask is not null)
                {
                    workpieceTask.Runtime.TaskState = WorkpieceState.Discarded;
                    workpieceTask.Runtime.CurrentLocation = WorkpieceLocation.Discarded;
                }

                _logger.Error(LogCategory.Task,
                    $"任务中止：{crane.Name} 任务 {task.TaskId:N} 原因：{reason}");
            }
        }
        finally
        {
            // 无论如何都要暂停线路并报警，避免半清理状态继续调度
            _lineControl.EnterFaultPause();
            _alarmManager.Raise(
                AlarmLevel.Error,
                crane.Name,
                reason,
                "已释放路权/预约/锁并标记该工件为已丢弃，线路暂停；现场残留实物取走后重新启动即可",
                crane.Id,
                true);
        }
    }

    /// <summary>
    /// 若设备当前工件正是被丢弃的工件，则清空设备内存引用。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <param name="workpieceRuntimeId">工件运行期标识</param>
    private void ClearDeviceWorkpiece(int deviceId, Guid workpieceRuntimeId)
    {
        var device = _deviceRegistry.Get(deviceId);
        if (device?.CurrentWorkpiece?.Id == workpieceRuntimeId)
        {
            device.CurrentWorkpiece = null;
        }
    }
}
