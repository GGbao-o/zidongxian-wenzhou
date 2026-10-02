using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车执行 Worker：**每台启用天车一个独立后台循环**，各自推进自己的任务（真正并行）。
/// 每台车有任务时一次跑完整条线性流程（见 <see cref="CraneStateMachine.RunFlowAsync"/>）。
/// </summary>
public sealed class CraneWorker : IHostedService
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>状态机（编排器）。</summary>
    private readonly CraneStateMachine _stateMachine;

    /// <summary>线路控制。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>各天车的执行循环。</summary>
    private readonly List<Task> _loops = new();

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="stateMachine">状态机编排器</param>
    /// <param name="lineControl">线路控制</param>
    /// <param name="configService">配置服务</param>
    /// <param name="logger">日志</param>
    public CraneWorker(
        CraneRegistry craneRegistry,
        CraneStateMachine stateMachine,
        LineControlService lineControl,
        IConfigService configService,
        IAppLogger logger)
    {
        _craneRegistry = craneRegistry;
        _stateMachine = stateMachine;
        _lineControl = lineControl;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>启动每台启用天车的执行循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

    /// <summary>单台天车的执行循环：有任务则一次跑完整条流程。</summary>
    /// <param name="crane">天车</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunCraneAsync(Crane crane, CancellationToken cancellationToken)
    {
        var interval = Math.Max(50, _configService.GetAppOptions().StateMachine.ExecuteIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            // 线路运行中且本车有任务 → 推进整条流程（一次 await 到底）
            if (_lineControl.IsRunning && crane.Runtime.CurrentTask is { } task)
            {
                try
                {
                    await _stateMachine.RunFlowAsync(crane, task, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 状态机内部已按二分处理；这里兜底，避免循环退出
                    _logger.Error(LogCategory.StateMachine, $"{crane.Name} 流程异常", ex);
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
}
