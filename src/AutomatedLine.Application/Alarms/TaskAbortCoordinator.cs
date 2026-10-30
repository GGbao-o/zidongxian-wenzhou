using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Alarms;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Line;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Alarms;

/// <summary>
/// 异常搬运清理协调器（架构文档 §10.4）。
/// 取料开始调用充磁前保留工件并恢复待派发，开始调用后丢弃工件、清理设备引用。
/// 两条路径均暂停线路、释放路权与设备预约、释放天车并报警。
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
    /// <param name="diagnostic">清理前固定的可选诊断，仅供展示。</param>
    public void Abort(Crane crane, string reason, AlarmDiagnostic? diagnostic = null)
        => HandleFailure(crane, reason, discardWorkpiece: true, diagnostic);

    /// <summary>终止充磁前的本次搬运，保留工件及原位置，等待人工恢复线路后重新派发。</summary>
    /// <param name="crane">发生异常的天车</param>
    /// <param name="reason">异常详情，写入日志及报警</param>
    /// <param name="diagnostic">清理前固定的可选诊断，仅供展示。</param>
    public void RecoverBeforeMagnet(Crane crane, string reason, AlarmDiagnostic? diagnostic = null)
        => HandleFailure(crane, reason, discardWorkpiece: false, diagnostic);

    /// <summary>共享资源释放与报警流程，只有弃板路径才清理设备工件引用和位置。</summary>
    private void HandleFailure(Crane crane, string reason, bool discardWorkpiece, AlarmDiagnostic? diagnostic)
    {
        var task = crane.Runtime.CurrentTask;
        // Worker兜底可能没有TransferContext，也在清引用前保留现有任务，不读取任何现场数据。
        diagnostic ??= new AlarmDiagnostic
        {
            TaskId = task?.TaskId, Workpiece = task is null ? null : _taskManager.Get(task.WorkpieceRuntimeId)?.Info.ToString(),
            Route = task is null ? null : $"设备{task.SourceDeviceId}→设备{task.TargetDeviceId}",
            LastStep = crane.Runtime.CurrentStep
        };
        try
        {
            // 先暂停再把工件恢复为 Waiting，避免清理期间马上被重新生成并派发。   线路只要暂停了  生成器下一轮发现线路未运行，会清空候选池
            _lineControl.EnterFaultPause();
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
                //充磁过  需要设备清理一下内存数据
                if (discardWorkpiece)
                {
                    // 只有充磁后弃板才清设备引用；充磁前工件仍属于源设备或上料队列。
                    ClearDeviceWorkpiece(task.SourceDeviceId, task.WorkpieceRuntimeId);
                    ClearDeviceWorkpiece(task.TargetDeviceId, task.WorkpieceRuntimeId);
                }

                // 两条路径都保留任务记录，分别恢复待派发或标记丢弃，方便追溯同一工件。
                var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId);
                if (workpieceTask is not null)
                {
                    //充磁过  工件的内存状态需要清除
                    if (discardWorkpiece)
                    {
                        //工件标记为已丢弃
                        workpieceTask.Runtime.TaskState = WorkpieceState.Discarded;
                        workpieceTask.Runtime.CurrentLocation = WorkpieceLocation.Discarded;
                    }
                    //没有充磁过 工件的状态回退
                    else
                    {
                        // 位置、工序和排位没有发生交接，只结束本次搬运而保留同一工件。   这样下一次还能继续派发
                        workpieceTask.Runtime.TaskState = WorkpieceState.Waiting;
                    }
                }

                // task.txt 区分搬运回退与工件丢弃，避免把充磁前故障误记成工件生命周期终点。
                // 注意日志在 if 之外（与原来一致：任务在表里已被移除时也要留痕），故这里必须空安全取工件文本
                var workpieceText = workpieceTask is null ? "工件任务已不在内存表" : $"工件 {workpieceTask.Info}";
                if (discardWorkpiece)
                {
                    _logger.Error(LogCategory.Task,
                        $"任务丢弃：{workpieceText}（任务 {task.TaskId.ToString("N")[..8]}），天车 {crane.Name}，原因：{reason}");
                }
                else
                {
                    _logger.Warn(LogCategory.Task,
                        $"搬运回退：{workpieceText}（任务 {task.TaskId.ToString("N")[..8]}），工件保留、状态回 Waiting，天车 {crane.Name}，原因：{reason}");
                }
            }
        }
        finally
        {
            // 无论如何都要暂停线路并报警，避免半清理状态继续调度
            if (_lineControl.State != LineState.PausedByFault)
            {
                _lineControl.EnterFaultPause();
            }
            _alarmManager.Raise(
                AlarmLevel.Error,
                crane.Name,
                reason,
                discardWorkpiece
                    ? "已释放路权/预约/锁并标记该工件为已丢弃，线路暂停；现场残留工件取走 确保天车在安全位置后重新启动即可"
                    : "尚未开始取料充磁，已释放路权/预约/锁；线路已暂停 工件置保留，PLC信号正确 天车在安全位置后重新启动线路可再次搬运",
                crane.Id,
                true,
                diagnostic);
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
        //判断是不是同一个工件 不是无脑清理
        if (device?.CurrentWorkpiece?.Id == workpieceRuntimeId)
        {
            device.CurrentWorkpiece = null;
        }
    }
}
