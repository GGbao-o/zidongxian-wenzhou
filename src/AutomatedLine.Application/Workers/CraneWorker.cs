using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Tasks;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车执行 Worker：**每台启用天车一个独立后台循环**，各自推进自己的任务（真正并行）。
/// 任务经 <see cref="CraneTaskChannels"/> 交接：Worker 出队后**独占**写本车 <see cref="Crane.Runtime"/>，
/// 一次跑完整条线性流程（见 <see cref="CraneStateMachine.RunFlowAsync"/>）。
/// 普通故障只中止当前搬运；人工恢复线路后继续消费新任务，宿主取消才退出循环。
/// </summary>
public sealed class CraneWorker : IHostedService
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>每车任务队列（交接通道）。</summary>
    private readonly CraneTaskChannels _channels;

    /// <summary>状态机（编排器）。</summary>
    private readonly CraneStateMachine _stateMachine;

    /// <summary>线路控制。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>异常协调器（兜底释放资源）。</summary>
    private readonly TaskAbortCoordinator _abortCoordinator;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>各天车的执行循环。</summary>
    private readonly List<Task> _loops = new();

    // 只登记已领取的搬运；每次领取创建新令牌，人工中止不影响本车下一单或其他车。
    private readonly object _executionSync = new();
    private readonly Dictionary<int, CraneTaskExecution> _executions = new();

    /// <summary>读取所选天车当前执行的搬运及人工中止状态，不读取 PLC。</summary>
    /// <param name="craneId">天车标识</param>
    /// <returns>执行中的搬运和中止标志；空闲或已结束时返回 null</returns>
    public (TransportTask Task, bool IsAbortRequested)? GetCurrentExecution(int craneId)
    {
        lock (_executionSync)
        {
            if (!_executions.TryGetValue(craneId, out var execution) || !execution.IsActive
                || _craneRegistry.Get(craneId)?.Runtime.CurrentTask?.TaskId != execution.Task.TaskId)
                return null;
            return (execution.Task, execution.IsManualAbortRequested);
        }
    }

    /// <summary>人工中止指定天车的当前搬运，立即暂停线路；不下发急停指令。</summary>
    /// <param name="craneId">天车标识</param>
    /// <param name="expectedTaskId">界面看到的搬运标识；拒绝误中止后来分配的新任务</param>
    /// <returns>首次接受中止返回 true；无任务、已结束、过期或重复请求返回 false</returns>
    public bool TryAbortCurrentTask(int craneId, Guid expectedTaskId)
    {
        CraneTaskExecution execution;
        lock (_executionSync)
        {
            if (!_executions.TryGetValue(craneId, out execution!)
                || execution.Task.TaskId != expectedTaskId
                || _craneRegistry.Get(craneId)?.Runtime.CurrentTask?.TaskId != expectedTaskId)
                return false;
        }
        // 不持有登记表锁触发线路/UI 事件，避免事件回读执行状态造成锁交错。
        if (!execution.TryRequestManualAbort(_lineControl.EnterFaultPause)) return false;
        _logger.Info(LogCategory.StateMachine,
            $"[天车{craneId}] 人工中止请求已接受：搬运 {expectedTaskId:N}，线路已暂停");
        return true;
    }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="channels">每车任务队列</param>
    /// <param name="stateMachine">状态机编排器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="configService">配置服务</param>
    /// <param name="abortCoordinator">异常协调器</param>
    /// <param name="logger">日志</param>
    public CraneWorker(
        CraneRegistry craneRegistry,
        CraneTaskChannels channels,
        CraneStateMachine stateMachine,
        LineControlService lineControl,
        IConfigService configService,
        TaskAbortCoordinator abortCoordinator,
        IAppLogger logger)
    {
        _craneRegistry = craneRegistry;
        _channels = channels;
        _stateMachine = stateMachine;
        _lineControl = lineControl;
        _configService = configService;
        _abortCoordinator = abortCoordinator;
        _logger = logger;
    }

    /// <summary>启动每台启用天车的执行循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        //对每一个天车启动task.run
        foreach (var crane in _craneRegistry.Snapshot().Where(c => c.IsEnabled))
        {
            var c = crane;
            _loops.Add(Task.Run(() => RunCraneAsync(c, _cts.Token), CancellationToken.None));
        }

        Console.WriteLine($"[CraneWorker] 已启动 {_loops.Count} 个天车执行循环");
        return Task.CompletedTask;
    }

    /// <summary>停止全部执行循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        foreach (var loop in _loops)
        {
            await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 单台天车的执行循环：线路运行中且队列有任务 → 出队、独占写本车 Runtime、一次跑完整条流程。
    /// </summary>
    /// <param name="crane">天车</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunCraneAsync(Crane crane, CancellationToken cancellationToken)
    {
        //天车状态机（Worker）推进周期（毫秒）。
        var interval = Math.Max(50, _configService.GetAppOptions().StateMachine.ExecuteIntervalMs);
        //天车任务队列 容量未1
        var channel = _channels.For(crane.Id);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_lineControl.IsRunning && channel.Reader.TryRead(out var task))
            {
                // 本车 Runtime 由本循环独占写（调度器不再碰它）
                crane.Runtime.CurrentTask = task;//设置天车任务
                crane.Runtime.State = CraneState.Reserved;//已分配任务  其实这时候 天车就不是空闲状态了   CraneScheduler集中式调度就不会再选择这个天车 
                var execution = new CraneTaskExecution(task, cancellationToken);
                lock (_executionSync) _executions[crane.Id] = execution;
                try
                {
                    //开始执行任务
                    await _stateMachine.RunFlowAsync(crane, task, execution.Token, execution).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 只有执行器真的被取消才退出；驱动内部取消按普通故障兜底，避免丢失后续任务的消费者。
                    FallbackAbort(crane, "任务被取消（兜底）");
                    ClearRuntime(crane);
                    throw;
                }
                catch (OperationCanceledException) when (execution.IsManualAbortRequested)
                {
                    // 人工中止只结束当前搬运；状态机已弃板，宿主循环继续等待后续启动。
                    FallbackAbort(crane, "人工中止当前任务（兜底）");
                    ClearRuntime(crane);
                }
                catch (Exception ex)
                {
                    // 状态机内部已处理；这里兜底（含释放资源），避免循环退出与资源泄漏
                    _logger.Error(LogCategory.StateMachine, $"{crane.Name} 流程异常（兜底）", ex);
                    //异常处理 兜底机制  Abort方法里面其实包含了ClearRuntime()
                    FallbackAbort(crane, ex.Message);
                    ClearRuntime(crane);
                }
                finally
                {
                    execution.Close();
                    lock (_executionSync) _executions.Remove(crane.Id);
                    try { await execution.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        // 取消回调失败也不能带走消费者；流程清理已完成，记录释放异常供排查。
                        _logger.Error(LogCategory.StateMachine, $"[{crane.Name}] 本次任务取消范围释放异常", ex);
                    }
                }
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>异常兜底：若还挂着任务，走统一中止（释放路权/预约、清理任务、暂停线路）。</summary>
    /// <param name="crane">天车</param>
    /// <param name="reason">原因</param>
    private void FallbackAbort(Crane crane, string reason)
    {
        if (crane.Runtime.CurrentTask is not null)
        {
            _abortCoordinator.Abort(crane, $"[{crane.Name}] 兜底异常：{reason}");
        }
    }

    /// <summary>异常兜底：清空本车任务态（幂等）。</summary>
    /// <param name="crane">天车</param>
    private static void ClearRuntime(Crane crane)
    {
        crane.Runtime.CurrentTask = null;
        crane.Runtime.State = CraneState.Idle;
        crane.Runtime.LockedInterval = null;
    }
}
