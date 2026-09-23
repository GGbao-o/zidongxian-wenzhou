using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Line;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace AutomatedLine.Application.Workers;

/// <summary>
/// 天车 Worker：按固定周期推进各天车的状态机（架构文档 §8.5「CraneWorker 与状态机」）。
/// 说明：P3 用一个循环统一推进每台天车一步；若后续需要各车独立节拍，再拆成"每车一个 Worker 任务"。
/// 只有线路处于自动运行时才推进。推进周期来自 <c>StateMachine.ExecuteIntervalMs</c>。
/// </summary>
public sealed class CraneWorker : IHostedService
{
    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>状态机。</summary>
    private readonly CraneStateMachine _stateMachine;

    /// <summary>线路控制。</summary>
    private readonly LineControlService _lineControl;

    /// <summary>配置服务。</summary>
    private readonly IConfigService _configService;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>循环任务。</summary>
    private Task? _loop;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="stateMachine">状态机</param>
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

    /// <summary>启动执行循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        Console.WriteLine("[CraneWorker] 天车状态机推进循环已启动");
        return Task.CompletedTask;
    }

    /// <summary>停止执行循环。</summary>
    /// <param name="cancellationToken">宿主取消令牌</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <summary>执行主循环。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = Math.Max(50, _configService.GetAppOptions().StateMachine.ExecuteIntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_lineControl.IsRunning)
            {
                foreach (var crane in _craneRegistry.Snapshot())
                {
                    if (crane.Runtime.CurrentTask is null)
                    {
                        continue;
                    }

                    try
                    {
                        await _stateMachine.AdvanceAsync(crane, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 状态机内部已捕获，这里兜底，避免执行循环退出
                        _logger.Error(LogCategory.StateMachine, $"{crane.Name} 状态机推进异常", ex);
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
}
