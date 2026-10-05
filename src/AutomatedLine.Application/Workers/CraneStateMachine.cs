using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Reservations;
using AutomatedLine.Application.RightOfWay;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Routing;
using AutomatedLine.Domain.Spatial;
using AutomatedLine.Domain.Tasks;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车搬运编排器（线性 async，架构文档 §8.5 / 《应用层设计.md》§5）。
/// 通用骨架：Acquire → Pick(源类型) → Transit → Place(目标类型) → Finish；
/// 取放料细节由 <see cref="ITransferHandler"/> 按设备类型实现。
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

    /// <summary>取放料策略注册表。</summary>
    private readonly TransferHandlerRegistry _handlers;

    /// <summary>配置存储（现查设备坐标）。</summary>
    private readonly IConfigurationStore _configStore;

    /// <summary>配置服务（程序参数）。</summary>
    private readonly IConfigService _configService;

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
    /// <param name="handlers">取放料策略注册表</param>
    /// <param name="configStore">配置存储</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public CraneStateMachine(
        DeviceRegistry deviceRegistry,
        DriverRegistry driverRegistry,
        IRuntimeTaskManager taskManager,
        RightOfWayManager rightOfWayManager,
        ResourceReservationManager reservationManager,
        TaskAbortCoordinator abortCoordinator,
        TransferHandlerRegistry handlers,
        IConfigurationStore configStore,
        IConfigService configService,
        IAppLogger logger)
    {
        _deviceRegistry = deviceRegistry;
        _driverRegistry = driverRegistry;
        _taskManager = taskManager;
        _rightOfWayManager = rightOfWayManager;
        _reservationManager = reservationManager;
        _abortCoordinator = abortCoordinator;
        _handlers = handlers;
        _configStore = configStore;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>执行一次完整搬运（从取料到完成）。</summary>
    /// <param name="crane">天车</param>
    /// <param name="task">搬运任务</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RunFlowAsync(Crane crane, TransportTask task, CancellationToken cancellationToken)
    {
        var wp = _taskManager.Get(task.WorkpieceRuntimeId)
                 ?? throw new InvalidOperationException("找不到工件任务");
        var source = _deviceRegistry.Get(task.SourceDeviceId)
                     ?? throw new InvalidOperationException($"源设备 {task.SourceDeviceId} 不存在");
        var target = _deviceRegistry.Get(task.TargetDeviceId)
                     ?? throw new InvalidOperationException($"目标设备 {task.TargetDeviceId} 不存在");
        //一次搬运流程的上下文
        var ctx = new TransferContext(crane, wp, _driverRegistry, _configStore, _configService.GetAppOptions(), _logger);
        var route = $"{wp.Info} {source.Name}→{target.Name}";

        // 标记本车归属（Worker 通常已设；此处兜底，且让异常协调器能取到当前任务）
        crane.Runtime.CurrentTask = task;

        // 1) 原子获取路权 + 预约；失败回滚、任务回 Waiting
        if (!Acquire(crane, task))
        {
            return;
        }

        crane.Runtime.State = CraneState.Running;
        wp.Runtime.TaskState = WorkpieceState.Running;
        //打印日志
        _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 任务开始：{route}");

        // 记录当前步骤（异常日志用），覆盖各阶段   phase是阶段 只是用来输出日志
        var phase = "未知";
        //已经拿路权 设备预约了 下面开始天车状态机了 天车开始执行任务
        try
        {
            // 2) 再审核源（内存一致；PLC 点位由 handler 首步等待）
            phase = $"审核源({source.Name})";
            AuditSource(source, wp);
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 审核源通过：{source.Name}");

            // 下发本车运动速度（每次执行前设置一次）
            phase = "设置速度";
            await ctx.SetCraneSpeedAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 设置速度完成");

            // 3) 取料：  已经z回升->发下料完成了
            phase = $"取料({source.Name})";
            await _handlers.Get(source.Type).PickAsync(ctx, source, cancellationToken).ConfigureAwait(false);
            source.CurrentWorkpiece = null;
            crane.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.OnCrane(crane.Id);
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 取料完成：{source.Name}");

            // 4) 中转：XY→目标、释放源预约、缩小路权、复位源设备握手位
            phase = $"中转({source.Name}→{target.Name})";
            await TransitAsync(ctx, crane, task, source, target, cancellationToken).ConfigureAwait(false);
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 中转到达：{target.Name}（已释放源预约、缩小路权、复位源握手位）");

            // 5) 放料：结果：也是z回升了发过下料完成的
            phase = $"放料({target.Name})";
            await _handlers.Get(target.Type).PlaceAsync(ctx, target, cancellationToken).ConfigureAwait(false);
            target.CurrentWorkpiece = wp;
            crane.CurrentWorkpiece = null;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(target.Id);
            wp.Runtime.CurrentProcess = task.TargetProcess;
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 放料完成：{target.Name}");

            // 6) 完成：回安全位、释放全部
            phase = "收尾";
            await FinishAsync(ctx, crane, task, target, wp, cancellationToken).ConfigureAwait(false);

            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 任务完成：{route}");
        }
        catch (OperationCanceledException)
        {
            var detail = BuildFailure(crane, ctx, route, phase, "任务被取消");
            _logger.Error(LogCategory.StateMachine, detail);
            _abortCoordinator.Abort(crane, detail);
            throw;
        }
        catch (Exception ex)
        {
            //构造异常详情 方便打印日志
            var detail = BuildFailure(crane, ctx, route, phase, ex.Message);
            _logger.Error(LogCategory.StateMachine, detail, ex);
            //异常处理机制 状态机发生异常了 下面的方法处理   1.释放路权 2.释放预约 3.清空天车内存状态 4.清空相关设备内存引用(ClearDeviceWorkpiece方法 是把device.CurrentWorkpiece=null) 5.标记工件已丢弃Discarded
            _abortCoordinator.Abort(crane, detail);   // 统一：释放天车/路权/预约 + 清理任务 + 暂停 + 报警
        }
    }

    /// <summary>构造异常详情（天车 / 工件 / 源→目标 / 步骤 / 原因 / 是否已充磁）。</summary>
    private static string BuildFailure(Crane crane, TransferContext ctx, string route, string phase, string reason)
        => $"[{crane.Name}] 任务 {route} 步骤[{phase}] 异常：{reason}"
           + (ctx.HasPieceOnCrane ? "（已充磁：板可能在天车上）" : "（未充磁：板应在源设备）");

    /// <summary>原子获取路权 + 源/目标预约；失败回滚并让出天车。</summary>
    private bool Acquire(Crane crane, TransportTask task)
    {
        _logger.Info(LogCategory.StateMachine,
            $"[{crane.Name}] 获取资源：任务 {task.TaskId:N}（源设备 {task.SourceDeviceId} → 目标设备 {task.TargetDeviceId}）");
        var interval = task.GetRightOfWayInterval(crane.Width);

        if (!_rightOfWayManager.TryAcquire(task.TaskId, interval))
        {
            //获取失败回滚 这里会将工件设置位waiting
            _logger.Warn(LogCategory.StateMachine, $"[{crane.Name}] 获取资源失败：路权区间被占用，回滚让出天车");
            Rollback(crane, task, "路权被占用");
            return false;//获取失败了
        }

        if (!_reservationManager.TryReserve(task.TaskId, task.SourceDeviceId, task.TargetDeviceId))
        {
            _logger.Warn(LogCategory.StateMachine, $"[{crane.Name}] 获取资源失败：源/目标设备已被预约，释放已占路权后回滚");
            _rightOfWayManager.Release(task.TaskId);
            Rollback(crane, task, "设备预约失败");
            return false;
        }

        crane.Runtime.LockedInterval = interval;
        _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 获取资源成功：路权 + 源/目标预约已锁定");
        return true;
    }

    /// <summary>获取失败回滚（尚未开始搬运）。</summary>
    private void Rollback(Crane crane, TransportTask task, string reason)
    {
        crane.Runtime.LockedInterval = null;
        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        //获取当前工件任务
        var wp = _taskManager.Get(task.WorkpieceRuntimeId);
        if (wp is not null)
        {
            //将工件任务重新设置为入线
            wp.Runtime.TaskState = WorkpieceState.Waiting;
        }

        _logger.Warn(LogCategory.StateMachine, $"{crane.Name} 获取资源失败（{reason}），回滚待重派");
    }

    /// <summary>再审核源设备内存一致（上料架队列除外）。</summary>
    private static void AuditSource(DeviceBase source, WorkpieceTask wp)
    {
        if (source.Type == DeviceType.LoadRack)
        {
            return;   // 队列工件不挂在设备内存上
        }

        if (source.CurrentWorkpiece is not null && source.CurrentWorkpiece.Id != wp.Id)
        {
            throw new InvalidOperationException($"源设备 {source.Name} 当前工件与任务不一致");
        }
    }

    /// <summary>
    /// 中转：XY→目标上方、释放源预约、路权缩小到车体+目标，并**尽力复位源设备的握手位**。
    /// <para>
    /// 清握手位放在这里（而不是取料末尾）的理由：
    /// ① 此刻距"写下 下料完成=1"已隔整段 XY 移动（通常 1~3s），PLC 必然已采到那个 1，**无需额外等待或延时**；
    /// ② 语义对齐——正好是"与源设备的业务关系结束"（源预约也在同一处释放）。
    /// </para>
    /// **只清源、绝不碰目标**：目标马上要上料，它的握手位正要被写 1。
    /// </summary>
    private async Task TransitAsync(TransferContext ctx, Crane crane, TransportTask task, DeviceBase source, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, $"中转：XY → 目标上方（安全高度 Z={crane.SafeZ}）");
        var (x, y, _) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "中转：已到达目标上方（Z 仍在安全高度）");

        //释放源设备预约 缩小路权
        ctx.LogStep(target, "中转：释放源设备预约 + 路权缩小到目标车体区间");
        _reservationManager.ReleaseSource(task.TaskId);
        _rightOfWayManager.Resize(task.TaskId, XInterval.FromTask(target.PositionX, target.PositionX, crane.Width));

        // 源设备这一轮彻底结束：把它"上位机写入的握手位"复位为 0（双头镗/斜床/研磨机有效，其余空操作）。
        // 内部逐点容错：清不掉只记警告、不影响本任务（最坏退回"残留 1"的旧行为）。
        ctx.LogStep(source, "中转：复位源设备握手位（双头镗/斜床/研磨机有效，其余无操作；避免残留 1 让下一轮上料收不到上升沿）");
        if (_driverRegistry.GetDevice(source.Id) is { } sourceDriver)
        {
            //清除上位机写入设备的信息
            await _handlers.Get(source.Type)
                .ClearHandshakeAsync(sourceDriver, source.Name, _logger, cancellationToken)
                .ConfigureAwait(false);
        }

        ctx.LogStep(target, "中转：完成（已释放源预约、缩小路权、复位源握手位）");
    }

    /// <summary>完成：回安全位、释放天车/路权/目标预约、改进度。</summary>
    private async Task FinishAsync(TransferContext ctx, Crane crane, TransportTask task, DeviceBase target, WorkpieceTask wp, CancellationToken cancellationToken)
    {
        ctx.LogStep($"收尾：回本车安全位（X={crane.SafeX}、Z={crane.SafeZ}）");
        // 回到本车安全位 X（同一目标 Y、Z 取安全高度）
        var (_, y, _) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(crane.SafeX, y, crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep("收尾：释放路权 + 目标设备预约 + 清空天车运行时状态");
        _rightOfWayManager.Release(task.TaskId);
        _reservationManager.Release(task.TaskId);

        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.LockedInterval = null;

        wp.Runtime.TaskState = task.TargetProcess == ProcessType.Unloading
            ? WorkpieceState.Completed
            : WorkpieceState.Waiting;

        ctx.LogStep($"收尾：工件状态置 {wp.Runtime.TaskState}（目标工序={task.TargetProcess}）");

        // task.txt：任务生命周期**终点**。只有"送到下料架"这一趟才是真正完成；
        // 其余工序搬完只是回 Waiting（等下一段搬运），不算完成、不写 task.txt。
        if (wp.Runtime.TaskState == WorkpieceState.Completed)
        {
            _logger.Info(LogCategory.Task, $"任务完成：工件 {wp.Info}（已送到下料架）");
        }

        _logger.Info(LogCategory.StateMachine, $"{crane.Name} 完成任务 {task.TaskId:N}");
    }

}
