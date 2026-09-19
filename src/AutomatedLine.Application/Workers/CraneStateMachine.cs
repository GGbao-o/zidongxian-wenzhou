using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车搬运状态机（架构文档 §8.5）。
/// 每周期只推进一步，绝不阻塞；路权在派发时已取得，这里从 PreCheck 开始。
/// 任何一步异常 → <see cref="TaskAbortCoordinator"/> 自动清理并暂停线路。
/// 取放料确认在 P3 由状态机按内存语义完成，并同步写模拟驱动的"有料"信号以保持界面一致；
/// 现场点位到位后改为读取 PLC 反馈判定。
/// </summary>
public sealed class CraneStateMachine
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>驱动注册表。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>任务总表。</summary>
    private readonly IRuntimeTaskManager _taskManager;

    /// <summary>路权管理器。</summary>
    private readonly RightOfWayManager _rightOfWayManager;

    /// <summary>预约管理器。</summary>
    private readonly ResourceReservationManager _reservationManager;

    /// <summary>异常协调器。</summary>
    private readonly TaskAbortCoordinator _abortCoordinator;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="taskManager">任务总表</param>
    /// <param name="rightOfWayManager">路权管理器</param>
    /// <param name="reservationManager">预约管理器</param>
    /// <param name="abortCoordinator">异常协调器</param>
    /// <param name="logger">日志</param>
    public CraneStateMachine(
        DeviceRegistry deviceRegistry,
        DriverRegistry driverRegistry,
        IRuntimeTaskManager taskManager,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        TaskAbortCoordinator abortCoordinator,
        IAppLogger logger)
    {
        _deviceRegistry = deviceRegistry;
        _driverRegistry = driverRegistry;
        _taskManager = taskManager;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _abortCoordinator = abortCoordinator;
        _logger = logger;
    }

    /// <summary>
    /// 推进天车状态机一步。
    /// </summary>
    /// <param name="crane">天车</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task AdvanceAsync(Crane crane, CancellationToken cancellationToken)
    {
        var task = crane.Runtime.CurrentTask;
        if (task is null)
        {
            return;
        }

        try
        {
            switch (crane.Runtime.Step)
            {
                case TransportStep.PreCheck:
                    await StepPreCheckAsync(crane, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.ConfirmSource:
                    await StepConfirmSourceAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.MoveToSource:
                    await StepMoveToSourceAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.PickHandshake:
                    await StepPickHandshakeAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.PickConfirmed:
                    await StepPickConfirmedAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.RetractZAfterPick:
                    await StepRetractZAsync(crane, task.SourceX, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.MoveToTarget:
                    await StepMoveToTargetAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.PlaceHandshake:
                    await StepPlaceHandshakeAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.PlaceConfirmed:
                    await StepPlaceConfirmedAsync(crane, task, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.RetractZAfterPlace:
                    await StepRetractZAsync(crane, task.TargetX, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.ReturnToSafeX:
                    await StepReturnToSafeXAsync(crane, cancellationToken).ConfigureAwait(false);
                    break;
                case TransportStep.ReleaseRightOfWay:
                    _rightOfWayManager.Release(task.TaskId);
                    crane.Runtime.LockedInterval = null;
                    crane.Runtime.Step = TransportStep.ReleaseReservation;
                    break;
                case TransportStep.ReleaseReservation:
                    _reservationManager.Release(task.TaskId);
                    crane.Runtime.Step = TransportStep.Complete;
                    break;
                case TransportStep.Complete:
                    Complete(crane, task);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 状态机内的任何异常都走统一中止流程（自动清理 + 暂停线路）
            _abortCoordinator.Abort(crane, ex.Message);
        }
    }

    /// <summary>预检查：连接与报警。</summary>
    private Task StepPreCheckAsync(Crane crane, CancellationToken cancellationToken)
    {
        if (crane.Signals is not null && (!crane.IsConnected || crane.Signals.GetBool("Alarm")))
        {
            throw new InvalidOperationException("预检查失败：天车未连接或报警");
        }

        crane.Runtime.Step = TransportStep.ConfirmSource;
        return Task.CompletedTask;
    }

    /// <summary>二次确认源设备状态。</summary>
    private Task StepConfirmSourceAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        var source = _deviceRegistry.Get(task.SourceDeviceId)
                     ?? throw new InvalidOperationException($"源设备 {task.SourceDeviceId} 不存在");

        if (source.CurrentWorkpiece is not null && source.CurrentWorkpiece.Id != task.WorkpieceRuntimeId)
        {
            throw new InvalidOperationException($"源设备 {source.Name} 当前工件与任务不一致");
        }

        crane.Runtime.Step = TransportStep.MoveToSource;
        return Task.CompletedTask;
    }

    /// <summary>移动到源设备。</summary>
    private async Task StepMoveToSourceAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        await WriteCraneTargetAsync(crane, task.SourceX, 0, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        crane.Runtime.Step = TransportStep.PickHandshake;
    }

    /// <summary>取料握手：下发取料请求与抓取。</summary>
    private async Task StepPickHandshakeAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        if (_driverRegistry.GetDevice(task.SourceDeviceId) is { } sourceDriver)
        {
            await sourceDriver.WriteSignalAsync("PickRequest", true, cancellationToken).ConfigureAwait(false);
        }

        if (_driverRegistry.GetCrane(crane.Id) is { } craneDriver)
        {
            await craneDriver.WriteSignalAsync("Grip", true, cancellationToken).ConfigureAwait(false);
        }

        crane.Runtime.Step = TransportStep.PickConfirmed;
    }

    /// <summary>取料确认：内存引用从设备转移到天车。</summary>
    private async Task StepPickConfirmedAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        var source = _deviceRegistry.Get(task.SourceDeviceId)
                     ?? throw new InvalidOperationException($"源设备 {task.SourceDeviceId} 不存在");
        var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId)
                            ?? throw new InvalidOperationException("取料时找不到工件任务");

        // 单点写入位置：源设备释放、天车持有
        source.CurrentWorkpiece = null;
        crane.CurrentWorkpiece = workpieceTask;
        workpieceTask.Runtime.CurrentLocation = WorkpieceLocation.OnCrane(crane.Id);

        // 同步模拟信号，保持大屏一致（真实实现改为读取 PLC 反馈）
        if (_driverRegistry.GetDevice(task.SourceDeviceId) is { } sourceDriver)
        {
            await sourceDriver.WriteSignalAsync("HasMaterial", false, cancellationToken).ConfigureAwait(false);
            await sourceDriver.WriteSignalAsync("PickRequest", false, cancellationToken).ConfigureAwait(false);
        }

        if (_driverRegistry.GetCrane(crane.Id) is { } craneDriver)
        {
            await craneDriver.WriteSignalAsync("HasMaterial", true, cancellationToken).ConfigureAwait(false);
        }

        crane.Runtime.Step = TransportStep.RetractZAfterPick;
    }

    /// <summary>回安全高度。</summary>
    private async Task StepRetractZAsync(Crane crane, double x, CancellationToken cancellationToken)
    {
        await WriteCraneTargetAsync(crane, x, 0, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        crane.Runtime.Step = crane.Runtime.Step == TransportStep.RetractZAfterPick
            ? TransportStep.MoveToTarget
            : TransportStep.ReturnToSafeX;
    }

    /// <summary>移动到目标设备。</summary>
    private async Task StepMoveToTargetAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        await WriteCraneTargetAsync(crane, task.TargetX, 0, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        crane.Runtime.Step = TransportStep.PlaceHandshake;
    }

    /// <summary>放料握手：下发放料请求与松开。</summary>
    private async Task StepPlaceHandshakeAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        if (_driverRegistry.GetDevice(task.TargetDeviceId) is { } targetDriver)
        {
            await targetDriver.WriteSignalAsync("PlaceRequest", true, cancellationToken).ConfigureAwait(false);
        }

        if (_driverRegistry.GetCrane(crane.Id) is { } craneDriver)
        {
            await craneDriver.WriteSignalAsync("Release", true, cancellationToken).ConfigureAwait(false);
        }

        crane.Runtime.Step = TransportStep.PlaceConfirmed;
    }

    /// <summary>放料确认：内存引用从天车转移到目标设备。</summary>
    private async Task StepPlaceConfirmedAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        var target = _deviceRegistry.Get(task.TargetDeviceId)
                     ?? throw new InvalidOperationException($"目标设备 {task.TargetDeviceId} 不存在");
        var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId)
                            ?? throw new InvalidOperationException("放料时找不到工件任务");

        target.CurrentWorkpiece = workpieceTask;
        crane.CurrentWorkpiece = null;
        workpieceTask.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(target.Id);
        workpieceTask.Runtime.CurrentProcess = task.TargetProcess;

        // 同步模拟信号
        if (_driverRegistry.GetDevice(task.TargetDeviceId) is { } targetDriver)
        {
            await targetDriver.WriteSignalAsync("HasMaterial", true, cancellationToken).ConfigureAwait(false);
            await targetDriver.WriteSignalAsync("PlaceRequest", false, cancellationToken).ConfigureAwait(false);
        }

        if (_driverRegistry.GetCrane(crane.Id) is { } craneDriver)
        {
            await craneDriver.WriteSignalAsync("HasMaterial", false, cancellationToken).ConfigureAwait(false);
        }

        crane.Runtime.Step = TransportStep.RetractZAfterPlace;
    }

    /// <summary>回到安全位 X。</summary>
    private async Task StepReturnToSafeXAsync(Crane crane, CancellationToken cancellationToken)
    {
        await WriteCraneTargetAsync(crane, crane.SafeX, 0, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        crane.Runtime.Step = TransportStep.ReleaseRightOfWay;
    }

    /// <summary>完成任务：清空天车任务态，工件回到等待以生成下一段搬运。</summary>
    private void Complete(Crane crane, TransportTask task)
    {
        var workpieceTask = _taskManager.Get(task.WorkpieceRuntimeId);
        if (workpieceTask is not null)
        {
            workpieceTask.Runtime.TaskState = task.TargetProcess == Domain.Routing.ProcessType.Unloading
                ? WorkpieceState.Completed
                : WorkpieceState.Waiting;
        }

        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.Step = TransportStep.Idle;

        _logger.Info(LogCategory.StateMachine, $"{crane.Name} 完成任务 {Short(task.TaskId)}");
    }

    /// <summary>写天车目标并触发一次启动运动脉冲。</summary>
    private async Task WriteCraneTargetAsync(Crane crane, double x, double y, double z, CancellationToken cancellationToken)
    {
        if (_driverRegistry.GetCrane(crane.Id) is not { } driver)
        {
            throw new InvalidOperationException($"天车 {crane.Id} 无驱动");
        }

        await driver.WriteSignalAsync("TargetX", x, cancellationToken).ConfigureAwait(false);
        await driver.WriteSignalAsync("TargetY", y, cancellationToken).ConfigureAwait(false);
        await driver.WriteSignalAsync("TargetZ", z, cancellationToken).ConfigureAwait(false);
        await driver.WriteSignalAsync("MoveStart", true, cancellationToken).ConfigureAwait(false);
        await driver.WriteSignalAsync("MoveStart", false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务标识短文本，用于日志。</summary>
    private static string Short(Guid taskId) => taskId.ToString("N")[..8];
}
