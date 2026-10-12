using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Line;
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

    /// <summary>搬运入口再次核对线路状态，避免出队与暂停之间的窗口导致未开始任务继续动作。</summary>
    private readonly LineControlService _lineControl;

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
    /// <param name="lineControl">全线共享的线路状态，入口未运行时只回滚分配</param>
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
        LineControlService lineControl,
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
        _lineControl = lineControl;
        _logger = logger;
    }

    /// <summary>执行一次完整搬运（从取料到完成）。</summary>
    /// <param name="crane">天车</param>
    /// <param name="task">搬运任务</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="execution">Worker 的本次执行范围；用于识别人工中止，独立调用时可省略</param>
    /// <returns>异步搬运任务；入口未运行只回滚分配，普通故障按充磁边界清理，人工中止始终弃板，取消清理后向 Worker 传播</returns>
    public async Task RunFlowAsync(Crane crane, TransportTask task, CancellationToken cancellationToken,
        CraneTaskExecution? execution = null)
    {
        // 初始化也可能失败，先记录归属，确保充磁前故障能释放本次任务并保留工件。
        crane.Runtime.CurrentTask = task;
        TransferContext? ctx = null;
        var route = $"设备{task.SourceDeviceId}→设备{task.TargetDeviceId}";
        var phase = "初始化";
        try
        {
            execution?.ThrowIfManualAbortRequested();
            if (!_lineControl.IsRunning)
            {
                phase = "启动检查";
                // 尚未获取资源或交接工件：只撤回分配，不把正常暂停升级为故障，也不触碰设备引用/PLC。
                // 先关闭按钮入口再回滚；已接受的人工中止或宿主取消仍进入原异常分支，不能被保留策略吞掉。
                execution?.Close();
                cancellationToken.ThrowIfCancellationRequested();
                execution?.ThrowIfManualAbortRequested();
                Rollback(crane, task, $"线路未运行（{_lineControl.State}），本次搬运暂不执行");
                return;
            }
            var wp = _taskManager.Get(task.WorkpieceRuntimeId)
                     ?? throw new InvalidOperationException("找不到工件任务");
            var source = _deviceRegistry.Get(task.SourceDeviceId)
                         ?? throw new InvalidOperationException($"源设备 {task.SourceDeviceId} 不存在");
            var target = _deviceRegistry.Get(task.TargetDeviceId)
                         ?? throw new InvalidOperationException($"目标设备 {task.TargetDeviceId} 不存在");
            route = $"{wp.Info} {source.Name}→{target.Name}";
            ctx = new TransferContext(crane, wp, _driverRegistry, _configStore, _configService.GetAppOptions(), _logger);

            // 1) 原子获取路权 + 预约；竞争失败回滚，任务回 Waiting，不作为故障暂停。
            phase = "获取资源";
            if (!Acquire(ctx, crane, task))
            {
                execution?.ThrowIfManualAbortRequested();
                return;
            }

            execution?.ThrowIfManualAbortRequested();

            crane.Runtime.State = CraneState.Running;
            wp.Runtime.TaskState = WorkpieceState.Running;
            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 任务开始：{route}");

            // 2) 再审核源（内存一致；PLC 点位由 handler 首步等待）
            phase = $"审核源({source.Name})";
            AuditSource(source, wp);
            ctx.LogStep($"审核源通过：{source.Name}");

            // 下发本车运动速度（每次执行前设置一次）
            phase = "设置速度";
            await ctx.SetCraneSpeedAsync(cancellationToken).ConfigureAwait(false);
            execution?.ThrowIfManualAbortRequested();
            ctx.LogStep("设置速度完成");

            // 3) 去源设备取料阶段：  已经z回升->发下料完成了
            phase = $"取料({source.Name})";
            await _handlers.Get(source.Type).PickAsync(ctx, source, cancellationToken).ConfigureAwait(false);
            execution?.ThrowIfManualAbortRequested();
            source.CurrentWorkpiece = null;
            crane.CurrentWorkpiece = wp;
            wp.Runtime.CurrentLocation = WorkpieceLocation.OnCrane(crane.Id);
            ctx.LogStep($"取料完成：{source.Name}（板已在天车 {crane.Name} 上）");

            // 4) 中转：XY→目标、释放源预约、缩小路权、清除源设备 目标设备的握手信号(源设备是下料 目标设备上料)
            phase = $"中转({source.Name}→{target.Name})";
            await TransitAsync(ctx, crane, task, source, target, cancellationToken).ConfigureAwait(false);
            execution?.ThrowIfManualAbortRequested();
            ctx.LogStep($"中转到达：{target.Name}（已释放源预约、缩小路权、复位源握手位）");

            // 5) 去目标设备放料阶段：结果：也是z回升了发过下料完成的
            phase = $"放料({target.Name})";
            await _handlers.Get(target.Type).PlaceAsync(ctx, target, cancellationToken).ConfigureAwait(false);
            execution?.ThrowIfManualAbortRequested();
            target.CurrentWorkpiece = wp;
            crane.CurrentWorkpiece = null;
            wp.Runtime.CurrentLocation = WorkpieceLocation.AtDevice(target.Id);
            wp.Runtime.CurrentProcess = task.TargetProcess;
            ctx.LogStep($"放料完成：{target.Name}（板已交给目标设备）");

            // 6) 完成：回安全位、释放全部
            phase = "收尾";
            await FinishAsync(ctx, crane, task, target, wp, cancellationToken, execution).ConfigureAwait(false);

            _logger.Info(LogCategory.StateMachine, $"[{crane.Name}] 任务完成：{route}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭中止入口后再选择清理策略，避免清理途中接受按钮导致弃板意图丢失。
            if (execution?.Close() == true)
            {
                var abortDetail = BuildFailure(crane, ctx, route, phase, "人工中止当前任务", manualAbort: true);
                _logger.Error(LogCategory.StateMachine, abortDetail);
                // 资源竞争回滚可能刚清掉归属；人工中止仍明确弃掉本次工件。
                crane.Runtime.CurrentTask = task;
                _abortCoordinator.Abort(crane, abortDetail);
                throw;
            }
            // 宿主取消需要向 Worker 传播；未取消的执行器遇到内部取消则走普通故障，保留真实原因。
            var detail = BuildFailure(crane, ctx, route, phase, "任务被取消");
            _logger.Error(LogCategory.StateMachine, detail);
            HandleFailure(crane, ctx, detail);
            throw;
        }
        catch (Exception ex)
        {
            if (execution?.Close() == true)
            {
                // 原动作失败与按钮中止同时发生时，已接受的人工弃板意图优先。
                var abortDetail = BuildFailure(crane, ctx, route, phase, $"人工中止当前任务；{ex.Message}", manualAbort: true);
                _logger.Error(LogCategory.StateMachine, abortDetail, ex);
                crane.Runtime.CurrentTask = task;
                _abortCoordinator.Abort(crane, abortDetail);
                return;
            }
            //构造异常详情 方便打印日志
            var detail = BuildFailure(crane, ctx, route, phase, ex.Message);
            _logger.Error(LogCategory.StateMachine, detail, ex);
            // PickAsync 开始调用充磁前保留工件；开始调用后（含命令或反馈失败）沿用弃板处理。
            HandleFailure(crane, ctx, detail);
        }
    }

    /// <summary>按 PickAsync 的充磁标记选择异常清理，初始化失败时尚未充磁，也必须保留工件。</summary>
    private void HandleFailure(Crane crane, TransferContext? ctx, string detail)
    {
        if (ctx?.MagnetCommandStarted == true)
        {
            _abortCoordinator.Abort(crane, detail);
        }
        else
        {
            _abortCoordinator.RecoverBeforeMagnet(crane, detail);
        }
    }

    /// <summary>任务标识短文本（前 8 位）。日志用：完整 GUID 有 32 位且人读不出信息。</summary>
    /// <param name="taskId">任务标识</param>
    /// <returns>8 位短号</returns>
    private static string ShortId(Guid taskId) => taskId.ToString("N")[..8];

    /// <summary>构造异常详情，标明取料是否已开始调用充磁，避免把反馈超时误报为充磁前。</summary>
    private static string BuildFailure(Crane crane, TransferContext? ctx, string route, string phase, string reason,
        bool manualAbort = false)
        => $"[{crane.Name}] 任务 {route} 步骤[{phase}] 异常：{reason}"
           + (manualAbort ? "（人工中止：工件丢弃）"
               : ctx?.MagnetCommandStarted == true ? "（取料充磁已开始：板可能在天车上）" : "（取料充磁未开始：工件保留在原位置）");

    /// <summary>原子获取路权 + 源/目标预约；失败回滚并让出天车。</summary>
    private bool Acquire(TransferContext ctx, Crane crane, TransportTask task)
    {
        ctx.LogStep($"获取资源：任务 {ShortId(task.TaskId)}（源设备 {task.SourceDeviceId} → 目标设备 {task.TargetDeviceId}）");
        var interval = task.GetRightOfWayInterval(crane.Width);

        if (!_rightOfWayManager.TryAcquire(task.TaskId, interval))
        {
            //获取失败回滚 这里会将工件设置位waiting
            ctx.LogStepWarn("获取资源失败：路权区间被占用，回滚让出天车");
            Rollback(crane, task, "路权被占用");
            return false;//获取失败了
        }

        if (!_reservationManager.TryReserve(task.TaskId, task.SourceDeviceId, task.TargetDeviceId))
        {
            ctx.LogStepWarn("获取资源失败：源/目标设备已被预约，释放已占路权后回滚");
            _rightOfWayManager.Release(task.TaskId);
            Rollback(crane, task, "设备预约失败");
            return false;
        }

        crane.Runtime.LockedInterval = interval;
        ctx.LogStep("获取资源成功：路权 + 源/目标预约已锁定");
        return true;
    }

    /// <summary>尚未开始搬运的分配回滚；用于入口未运行或资源竞争失败，保留设备引用和工件位置/工序/排位。</summary>
    /// <param name="crane">本次搬运使用的天车。</param>
    /// <param name="task">需回滚的搬运分配。</param>
    /// <param name="reason">回滚原因，记录日志但不触发故障报警。</param>
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

        // 带工件标识；与上面 Acquire 的"获取资源失败（成因）"配对：一行说成因、一行说回滚结果
        _logger.Warn(LogCategory.StateMachine,
            $"[{crane.Name}] 工件 {(wp is null ? "(已不在内存表)" : wp.Info.ToString())} 已回滚（{reason}）：任务回 Waiting、天车让出待下轮重派");
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
    /// 中转：XY→目标上方、先尽力复位源握手，再释放源预约及缩小路权，最后复位目标握手。
    /// <para>
    /// 清握手位放在这里（而不是取料末尾）的理由：
    /// ① 此刻距"写下 下料完成=1"已隔整段 XY 移动（通常 1~3s），PLC 必然已采到那个 1，**无需额外等待或延时**；
    /// ② 源握手复位结束后才释放源预约，避免另一任务取得设备后被旧流程清零。
    /// </para>
    /// **源与目标两侧都清，顺序先源后目标**：
    /// 源侧清掉刚写出的"下料完成"等位；目标侧清掉**上一轮残留**，
    /// 保证放料阶段每次写 1 都是干净的 **0→1 上升沿**——残留 1 会让机器收不到这次下发，
    /// 白等到握手超时（60s）后统一中止，冷启动、上一轮失败中止后尤其常见。
    /// </summary>
    private async Task TransitAsync(TransferContext ctx, Crane crane, TransportTask task, DeviceBase source, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, $"中转：XY → 目标上方（安全高度 Z={crane.SafeZ}）");
        var (x, y, _) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, crane.SafeZ, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "中转：已到达目标上方（Z 仍在安全高度）");

        // 清源握手时继续持有预约，保证旧任务所有源写入结束前不会交给下一任务。
        // 内部逐点容错：清不掉只记警告、不影响本任务（最坏退回"残留 1"的旧行为）。
        ctx.LogStep(source, "中转：复位源设备握手位（双头镗/斜床/研磨机有效，其余无操作；避免残留 1 让下一轮上料收不到上升沿）");
        //
        if (_driverRegistry.GetDevice(source.Id) is { } sourceDriver)
        {
            //清除上位机写入设备的信息
            await _handlers.Get(source.Type)
                .ClearHandshakeAsync(sourceDriver, source.Name, _logger, cancellationToken)
                .ConfigureAwait(false);
        }

        // 源侧异步写入已全部结束，此时让出设备和区间，下一任务才可开始交互。
        ctx.LogStep(target, "中转：源握手复位结束，释放源设备预约 + 路权缩小到目标车体区间");
        _reservationManager.ReleaseSource(task.TaskId);
        var targetInterval = XInterval.FromTask(target.PositionX, target.PositionX, crane.Width);
        if (_rightOfWayManager.Resize(task.TaskId, targetInterval))
        {
            // 大屏读取天车运行态；仅在调整成功后同步，避免显示未实际取得的区间。
            crane.Runtime.LockedInterval = targetInterval;
        }

        // 目标设备：放料前把它"上位机写入的握手位"清零，保证放料阶段每次写 1 都是干净的上升沿
        // （同样只对双头镗/斜床/研磨机有效；打号机、下料架为空操作）。
        // 顺序：先清源（下料完成已发出、PLC 已采到）→ 再清目标（清上一轮残留，为即将到来的上料做准备）。
        ctx.LogStep(target, "中转：复位目标设备握手位（清上一轮残留，保证上料写 1 是干净上升沿）");
        if (_driverRegistry.GetDevice(target.Id) is { } targetDriver)
        {
            await _handlers.Get(target.Type)
                .ClearHandshakeAsync(targetDriver, target.Name, _logger, cancellationToken)
                .ConfigureAwait(false);
        }

        ctx.LogStep(target, "中转：完成（已释放源预约、缩小路权、复位源与目标握手位）");
    }

    /// <summary>完成：回安全位、释放天车/路权/目标预约、改进度。</summary>
    private async Task FinishAsync(TransferContext ctx, Crane crane, TransportTask task, DeviceBase target, WorkpieceTask wp,
        CancellationToken cancellationToken, CraneTaskExecution? execution)
    {
        ctx.LogStep($"收尾：回本车安全位（X={crane.SafeX}、Z={crane.SafeZ}）");
        // 回到本车安全位 X（同一目标 Y、Z 取安全高度）
        var (_, y, _) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(crane.SafeX, y, crane.SafeZ, cancellationToken).ConfigureAwait(false);

        // 只有正常完成与人工中止的最终提交互斥；原有运动和收尾内容不变。
        if (execution is null) CommitCompletion();
        else execution.Complete(CommitCompletion);

        void CommitCompletion()
        {
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

            ctx.LogStep($"完成任务（任务 {ShortId(task.TaskId)}）");
        }
    }

}
